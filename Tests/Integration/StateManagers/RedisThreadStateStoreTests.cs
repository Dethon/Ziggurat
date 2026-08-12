using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

[Trait("Category", "Integration")]
public class RedisThreadStateStoreTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    private RedisThreadStateStore NewStore(
        TimeProvider? time = null,
        int snippetLength = 120,
        TimeSpan? archiveHorizon = null,
        TimeSpan? purgeHorizon = null) =>
        new(redisFixture.Connection,
            new RetentionSettings
            {
                // The shipped horizon unless a test is about purging: it is what the index trim
                // reads, so a short one here would take every seeded topic with it.
                PurgeHorizon = purgeHorizon ?? TimeSpan.FromDays(365),
                SnippetLength = snippetLength,
                ArchiveHorizon = archiveHorizon ?? TimeSpan.FromDays(182)
            },
            time ?? TimeProvider.System);

    [Fact]
    public async Task AppendMessagesAsync_ToFreshKey_StoresMessagesInOrder()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key,
        [
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "hi there")
        ]);

        var messages = await store.GetMessagesAsync(key);
        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["hello", "hi there"]);
        messages.Select(m => m.Role).ShouldBe([ChatRole.User, ChatRole.Assistant]);
    }

    [Fact]
    public async Task AppendMessagesAsync_AppendsToExistingList_PreservesOrder()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "first")]);
        await store.AppendMessagesAsync(key,
        [
            new ChatMessage(ChatRole.Assistant, "second"),
            new ChatMessage(ChatRole.User, "third")
        ]);

        var messages = await store.GetMessagesAsync(key);
        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["first", "second", "third"]);
    }

    [Fact]
    public async Task SetMessagesAsync_ReplacesExistingHistory()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "will be replaced")]);
        await store.SetMessagesAsync(key,
        [
            new ChatMessage(ChatRole.User, "replacement a"),
            new ChatMessage(ChatRole.Assistant, "replacement b")
        ]);

        var messages = await store.GetMessagesAsync(key);
        messages.ShouldNotBeNull();
        messages.Select(m => m.Text).ShouldBe(["replacement a", "replacement b"]);
    }

    [Fact]
    public async Task AppendMessagesAsync_SetsExpiration()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();

        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "ttl check")]);

        var ttl = await redisFixture.Connection.GetDatabase().KeyTimeToLiveAsync(key);
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetTailMessagesAsync_ListLongerThanMax_ReturnsOnlyTailInOrder()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        await store.AppendMessagesAsync(key,
            [.. Enumerable.Range(0, 10).Select(i => new ChatMessage(ChatRole.User, $"m{i}"))]);

        var tail = await store.GetTailMessagesAsync(key, 3);

        tail.ShouldNotBeNull();
        tail.Select(m => m.Text).ShouldBe(["m7", "m8", "m9"]);
    }

    [Fact]
    public async Task GetTailMessagesAsync_MaxLargerThanList_ReturnsAllMessages()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "only")]);

        var tail = await store.GetTailMessagesAsync(key, 50);

        tail.ShouldNotBeNull();
        tail.ShouldHaveSingleItem().Text.ShouldBe("only");
    }

    [Fact]
    public async Task GetMessageCountAsync_ReturnsListLength_AndZeroForMissingKey()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        (await store.GetMessageCountAsync(key)).ShouldBe(0);

        await store.AppendMessagesAsync(key,
            [new ChatMessage(ChatRole.User, "a"), new ChatMessage(ChatRole.Assistant, "b")]);

        (await store.GetMessageCountAsync(key)).ShouldBe(2);
    }

    [Fact]
    public async Task GetTopicPageAsync_FiltersBySpaceSlug()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        await store.SaveTopicAsync(new TopicMetadata("t-s1", 300, 0, "agent-slug", "Space1", now, null,
            SpaceSlug: "space-a"));
        await store.SaveTopicAsync(new TopicMetadata("t-s2", 301, 0, "agent-slug", "Space2", now, null,
            SpaceSlug: "space-b"));

        var filtered = (await store.GetTopicPageAsync("agent-slug", "space-a", null, 10)).Topics;

        filtered.ShouldContain(t => t.TopicId == "t-s1");
        filtered.ShouldNotContain(t => t.TopicId == "t-s2");
    }

    [Fact]
    public async Task GetHistoryAsync_ProjectsTheStoredConversation()
    {
        var store = NewStore();
        await store.AppendMessagesAsync(HistoryKey("agent-hist", 900),
        [
            new ChatMessage(ChatRole.User, "hello there"),
            new ChatMessage(ChatRole.Assistant, "hi, how can I help?")
        ]);

        var history = await store.GetHistoryAsync("agent-hist", 900, 0);

        history.Select(h => h.Content).ShouldBe(["hello there", "hi, how can I help?"]);
    }

    // The read keeps only text and used to discard a message whose text was empty, which would
    // make an image-only message vanish on reload. The transcript is a record of what was sent.
    [Fact]
    public async Task GetHistoryAsync_AMessageWithAttachmentsAndNoText_SurvivesTheRead()
    {
        var store = NewStore();
        var message = new ChatMessage(ChatRole.User, "");
        message.SetAttachments([
            new AttachmentReference
            {
                Id = "901-0/abc", FileName = "photo.png", MediaType = "image/png", SizeBytes = 4
            }
        ]);

        await store.AppendMessagesAsync(HistoryKey("agent-attach", 901), [message]);

        var read = (await store.GetHistoryAsync("agent-attach", 901, 0)).ShouldHaveSingleItem();
        read.Content.ShouldBeNullOrEmpty();
        read.Attachments!.Single().FileName.ShouldBe("photo.png");
    }

    [Fact]
    public async Task GetHistoryAsync_ProjectsAttachmentsAlongsideTheText()
    {
        var store = NewStore();
        var message = new ChatMessage(ChatRole.User, "what is in this?");
        message.SetAttachments([
            new AttachmentReference
            {
                Id = "902-0/def", FileName = "scan.pdf", MediaType = "application/pdf", SizeBytes = 9
            }
        ]);

        await store.AppendMessagesAsync(HistoryKey("agent-attach-text", 902), [message]);

        var read = (await store.GetHistoryAsync("agent-attach-text", 902, 0)).ShouldHaveSingleItem();
        read.Content.ShouldBe("what is in this?");
        read.Attachments!.Single().MediaType.ShouldBe("application/pdf");
    }

    // A topic driven by voice or by a schedule has no browser writing its last-message time, so
    // before this it sorted and aged by when it was created however much was said in it.
    [Fact]
    public async Task AppendMessagesAsync_StampsTheTopicsLastWriteTime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        await store.SaveTopicAsync(new TopicMetadata(
            "t-stamp", 500, 7, "agent-stamp", "Kitchen", clock.GetUtcNow(), LastMessageAt: null));

        clock.Advance(TimeSpan.FromHours(3));
        await store.AppendMessagesAsync(
            HistoryKey("agent-stamp", 500, 7), [new ChatMessage(ChatRole.User, "put the kettle on")]);

        var topic = ((await store.GetTopicPageAsync("agent-stamp", "default", null, 10)).Topics).ShouldHaveSingleItem();
        topic.LastMessageAt.ShouldBe(clock.GetUtcNow());
    }

    [Fact]
    public async Task ATopicWrittenToWithNoBrowserAttached_OrdersAheadOfANewerOne()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        await store.SaveTopicAsync(new TopicMetadata(
            "t-old", 510, 0, "agent-order", "Older", clock.GetUtcNow(), LastMessageAt: null));

        clock.Advance(TimeSpan.FromHours(1));
        await store.SaveTopicAsync(new TopicMetadata(
            "t-new", 511, 0, "agent-order", "Newer", clock.GetUtcNow(), LastMessageAt: null));

        clock.Advance(TimeSpan.FromHours(1));
        await store.AppendMessagesAsync(
            HistoryKey("agent-order", 510), [new ChatMessage(ChatRole.User, "still talking")]);

        var topics = (await store.GetTopicPageAsync("agent-order", "default", null, 10)).Topics;
        topics.Select(t => t.TopicId).ShouldBe(["t-old", "t-new"]);
    }

    // The index is the list. A topic record sitting in the store that nothing put in the index
    // does not exist to anyone looking, which is what makes the scan removable rather than a
    // fallback nobody can prove is dead.
    [Fact]
    public async Task GetTopicPageAsync_ATopicRecordThatWasNeverIndexed_IsNotListed()
    {
        var store = NewStore();
        await WriteTopicRecordDirectlyAsync(new TopicMetadata(
            "t-unindexed", 520, 0, "agent-unindexed", "Ghost", DateTimeOffset.UtcNow, null));

        ((await store.GetTopicPageAsync("agent-unindexed", "default", null, 10)).Topics).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteTopicAsync_RemovesTheTopicFromTheIndex()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t-gone", 530, 0, "agent-delete", "Going", DateTimeOffset.UtcNow, null));

        await store.DeleteTopicAsync("agent-delete", 530, "t-gone");

        ((await store.GetTopicPageAsync("agent-delete", "default", null, 10)).Topics).ShouldBeEmpty();
    }

    // Upgrading must not hide conversations: what is already stored has no index entry, and a
    // channel reading only the index would serve an empty sidebar until something wrote to each
    // topic. The migration builds the index from the records that already exist.
    [Fact]
    public async Task MigrateTopicsAsync_IndexesTopicsThatAlreadyExist()
    {
        var store = NewStore();
        var older = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteTopicRecordDirectlyAsync(new TopicMetadata(
            "t-existing-a", 540, 0, "agent-migrate", "First", older, older.AddHours(1)));
        await WriteTopicRecordDirectlyAsync(new TopicMetadata(
            "t-existing-b", 541, 0, "agent-migrate", "Second", older, older.AddHours(5)));

        await store.MigrateTopicsAsync();

        var topics = (await store.GetTopicPageAsync("agent-migrate", "default", null, 10)).Topics;
        topics.Select(t => t.TopicId).ShouldBe(["t-existing-b", "t-existing-a"]);
    }

    // Written on the same path that stamps the last-write time, so a row can be drawn without
    // reading a single message.
    [Fact]
    public async Task AppendMessagesAsync_StoresASnippetOfWhatWasLastSaid()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 3, 9, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock, snippetLength: 12);
        await store.SaveTopicAsync(new TopicMetadata(
            "t-snip", 560, 0, "agent-snippet", "Kitchen", clock.GetUtcNow(), null));

        await store.AppendMessagesAsync(HistoryKey("agent-snippet", 560),
        [
            new ChatMessage(ChatRole.User, "what is the weather"),
            new ChatMessage(ChatRole.Assistant, "cold and bright all afternoon")
        ]);

        var topic = (await store.GetTopicPageAsync("agent-snippet", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.LastMessageSnippet.ShouldBe("cold and bri");
    }

    [Fact]
    public async Task MigrateTopicsAsync_BackfillsSnippetsForTopicsThatAlreadyExist()
    {
        var store = NewStore();
        var when = new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero);
        await WriteTopicRecordDirectlyAsync(new TopicMetadata(
            "t-backfill", 570, 0, "agent-backfill", "Old", when, when));
        await store.AppendMessagesAsync(HistoryKey("agent-backfill", 570),
            [new ChatMessage(ChatRole.Assistant, "the last thing said")]);

        await store.MigrateTopicsAsync();

        var topic = (await store.GetTopicPageAsync("agent-backfill", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.LastMessageSnippet.ShouldBe("the last thing said");
    }

    // Unread is a subtraction of two numbers carried on the topic, so a badge reads no messages.
    [Fact]
    public async Task AppendMessagesAsync_CountsWhatTheTopicHolds()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t-count", 580, 0, "agent-count", "Counting", DateTimeOffset.UtcNow, null));

        await store.AppendMessagesAsync(HistoryKey("agent-count", 580),
            [new ChatMessage(ChatRole.User, "one"), new ChatMessage(ChatRole.Assistant, "two")]);

        var topic = (await store.GetTopicPageAsync("agent-count", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.MessageCount.ShouldBe(2);
        topic.ReadPosition.ShouldBe(0);
    }

    // The read position is set from what the store knows rather than from what a browser
    // believed a moment ago, so a reply that landed between the two is not counted unread.
    [Fact]
    public async Task MarkTopicReadAsync_MovesTheReadPositionToTheMessageCount()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t-read", 590, 0, "agent-read", "Reading", DateTimeOffset.UtcNow, null));
        await store.AppendMessagesAsync(HistoryKey("agent-read", 590),
            [new ChatMessage(ChatRole.User, "one"), new ChatMessage(ChatRole.Assistant, "two")]);

        await store.MarkTopicReadAsync("agent-read", 590, "t-read");

        var topic = (await store.GetTopicPageAsync("agent-read", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.ReadPosition.ShouldBe(topic.MessageCount);
    }

    // Nobody's stored read position survives the change of meaning, so everything is marked read
    // once rather than resolved. A sidebar full of badges nobody earned is worse than none.
    [Fact]
    public async Task MigrateTopicsAsync_MarksEveryExistingTopicFullyRead()
    {
        var store = NewStore();
        var when = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero);
        await WriteTopicRecordDirectlyAsync(new TopicMetadata(
            "t-allread", 600, 0, "agent-allread", "Old", when, when));
        await store.AppendMessagesAsync(HistoryKey("agent-allread", 600),
            [new ChatMessage(ChatRole.User, "said before the change")]);

        await store.MigrateTopicsAsync();

        var topic = (await store.GetTopicPageAsync("agent-allread", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.MessageCount.ShouldBe(1);
        topic.ReadPosition.ShouldBe(1);
    }

    // Keyset paging over the structure that already defines the order, so reaching page five
    // costs what page one costs.
    [Fact]
    public async Task GetTopicPageAsync_ReturnsOnePageAndACursorForTheNext()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        await SeedTopicsAsync(store, clock, "agent-page", count: 5);

        var first = await store.GetTopicPageAsync("agent-page", "default", cursor: null, pageSize: 2);

        first.Topics.Select(t => t.Name).ShouldBe(["topic-4", "topic-3"]);
        first.NextCursor.ShouldNotBeNull();

        var second = await store.GetTopicPageAsync("agent-page", "default", first.NextCursor, pageSize: 2);
        second.Topics.Select(t => t.Name).ShouldBe(["topic-2", "topic-1"]);

        var third = await store.GetTopicPageAsync("agent-page", "default", second.NextCursor, pageSize: 2);
        third.Topics.Select(t => t.Name).ShouldBe(["topic-0"]);
        third.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task GetTopicPageAsync_ATopicWrittenToAfterBeingPagedPast_MovesToTheTop()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        await SeedTopicsAsync(store, clock, "agent-bump", count: 3);

        clock.Advance(TimeSpan.FromHours(1));
        await store.AppendMessagesAsync(
            HistoryKey("agent-bump", 600), [new ChatMessage(ChatRole.User, "back again")]);

        var page = await store.GetTopicPageAsync("agent-bump", "default", cursor: null, pageSize: 3);

        page.Topics.Select(t => t.Name).ShouldBe(["topic-0", "topic-2", "topic-1"]);
    }

    // Archived is where a topic sits in the index and never a state it carries: the cutoff is
    // subtracted from the current time when the range is built, so the boundary moves with the
    // clock and nothing is written on either side of it.
    [Fact]
    public async Task GetTopicPageAsync_ATopicOlderThanTheArchiveHorizon_LeavesTheOrdinaryList()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock, archiveHorizon: TimeSpan.FromDays(30));
        await store.SaveTopicAsync(Topic("t-fresh", 610, "agent-archive", clock.GetUtcNow()));
        await store.SaveTopicAsync(Topic("t-stale", 611, "agent-archive", clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromDays(31));
        await store.AppendMessagesAsync(
            HistoryKey("agent-archive", 610), [new ChatMessage(ChatRole.User, "still here")]);

        var ordinary = await store.GetTopicPageAsync("agent-archive", "default", null, 10);
        var archived = await store.GetTopicPageAsync("agent-archive", "default", null, 10, archived: true);

        ordinary.Topics.Select(t => t.TopicId).ShouldBe(["t-fresh"]);
        archived.Topics.Select(t => t.TopicId).ShouldBe(["t-stale"]);
    }

    // No archive verb and no unarchive verb: the write that moved its score is the whole of it.
    [Fact]
    public async Task AppendMessagesAsync_ToAnArchivedTopic_ReturnsItToTheOrdinaryList()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock, archiveHorizon: TimeSpan.FromDays(30));
        await store.SaveTopicAsync(Topic("t-back", 620, "agent-unarchive", clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromDays(31));
        (await store.GetTopicPageAsync("agent-unarchive", "default", null, 10)).Topics.ShouldBeEmpty();

        await store.AppendMessagesAsync(
            HistoryKey("agent-unarchive", 620), [new ChatMessage(ChatRole.User, "back again")]);

        (await store.GetTopicPageAsync("agent-unarchive", "default", null, 10))
            .Topics.Select(t => t.TopicId).ShouldBe(["t-back"]);
        (await store.GetTopicPageAsync("agent-unarchive", "default", null, 10, archived: true))
            .Topics.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTopicPageAsync_TheArchivedRange_PagesLikeTheOrdinaryOne()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock, archiveHorizon: TimeSpan.FromDays(30));
        await SeedTopicsAsync(store, clock, "agent-archive-page", count: 3);

        clock.Advance(TimeSpan.FromDays(31));

        var first = await store.GetTopicPageAsync(
            "agent-archive-page", "default", null, 2, archived: true);
        var second = await store.GetTopicPageAsync(
            "agent-archive-page", "default", first.NextCursor, 2, archived: true);

        first.Topics.Select(t => t.Name).ShouldBe(["topic-2", "topic-1"]);
        second.Topics.Select(t => t.Name).ShouldBe(["topic-0"]);
    }

    // Expiry drops a topic's record but leaves its index member behind, and those members sit
    // below the archive horizon where nothing reads — so nothing would ever notice them. Score is
    // last write, so everything below the purge cutoff is expired by definition and one range
    // removal takes the lot without scanning anything.
    [Fact]
    public async Task GetTopicPageAsync_TrimsIndexEntriesBelowThePurgeCutoff()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock, archiveHorizon: TimeSpan.FromDays(30), purgeHorizon: TimeSpan.FromDays(60));
        await store.SaveTopicAsync(Topic("t-purged", 630, "agent-purge", clock.GetUtcNow()));
        var indexKey = "topics:agent-purge:default";
        (await redisFixture.Connection.GetDatabase().SortedSetLengthAsync(indexKey)).ShouldBe(1);

        clock.Advance(TimeSpan.FromDays(61));
        await store.GetTopicPageAsync("agent-purge", "default", null, 10);

        (await redisFixture.Connection.GetDatabase().SortedSetLengthAsync(indexKey)).ShouldBe(0);
    }

    [Fact]
    public async Task SaveTopicAsync_SetsTheTopicToExpireOnThePurgeHorizon()
    {
        var store = NewStore(purgeHorizon: TimeSpan.FromDays(365));
        await store.SaveTopicAsync(Topic("t-ttl", 640, "agent-ttl", DateTimeOffset.UtcNow));

        var ttl = await redisFixture.Connection.GetDatabase()
            .KeyTimeToLiveAsync("topic:agent-ttl:640:t-ttl");

        ttl.ShouldNotBeNull();
        ttl!.Value.ShouldBeGreaterThan(TimeSpan.FromDays(364));
    }

    private static TopicMetadata Topic(string topicId, long chatId, string agentId, DateTimeOffset at) =>
        new(topicId, chatId, 0, agentId, topicId, at, at);

    // Chat ids ascend with the index so a bump can be aimed at a known conversation.
    private static async Task SeedTopicsAsync(
        RedisThreadStateStore store, FakeTimeProvider clock, string agentId, int count)
    {
        foreach (var i in Enumerable.Range(0, count))
        {
            await store.SaveTopicAsync(new TopicMetadata(
                $"t-{agentId}-{i}", 600 + i, 0, agentId, $"topic-{i}", clock.GetUtcNow(), clock.GetUtcNow()));
            clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    // Bypasses SaveTopicAsync on purpose: this is what an upgrade finds in the store, a record
    // written before the index existed.
    private async Task WriteTopicRecordDirectlyAsync(TopicMetadata topic)
    {
        await redisFixture.Connection.GetDatabase().StringSetAsync(
            $"topic:{topic.AgentId}:{topic.ChatId}:{topic.TopicId}",
            System.Text.Json.JsonSerializer.Serialize(topic));
    }

    private static string HistoryKey(string agentId, long chatId, long threadId = 0) =>
        new AgentKey($"{chatId}:{threadId}", agentId).ToString();
}