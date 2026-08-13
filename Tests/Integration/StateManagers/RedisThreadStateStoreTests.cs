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
        topic.LastMessageSnippet.ShouldBe("cold and bri…");
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

    // A badge means what a person would count on screen. The stored list also holds tool and
    // system turns — and a tool-using turn's assistant-role function calls, which the transcript
    // hides — so counting by role alone would badge what the reader is never shown.
    [Fact]
    public async Task AppendMessagesAsync_CountsOnlyTheMessagesAReaderIsShown()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t-tools", 585, 0, "agent-tools", "Tooling", DateTimeOffset.UtcNow, null));

        await store.AppendMessagesAsync(HistoryKey("agent-tools", 585),
        [
            new ChatMessage(ChatRole.User, "what is on tonight"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup")]),
            new ChatMessage(ChatRole.Tool, "looked it up"),
            new ChatMessage(ChatRole.System, "a note to itself"),
            new ChatMessage(ChatRole.Assistant, "two things")
        ]);

        var topic = (await store.GetTopicPageAsync("agent-tools", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.MessageCount.ShouldBe(2);
    }

    // An index member whose record is gone is one fewer row, not the end of the range. Deciding
    // the cursor on rows rather than members would stop the list short of everything below it.
    [Fact]
    public async Task GetTopicPageAsync_AnIndexMemberWhoseRecordIsGone_DoesNotEndTheRange()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        await SeedTopicsAsync(store, clock, "agent-dangling", count: 4);

        // The record alone, the way key expiry leaves it: the member stays in the index.
        await redisFixture.Connection.GetDatabase()
            .KeyDeleteAsync("topic:agent-dangling:601:t-agent-dangling-1");

        var first = await store.GetTopicPageAsync("agent-dangling", "default", null, 2);
        var second = await store.GetTopicPageAsync("agent-dangling", "default", first.NextCursor, 2);

        first.NextCursor.ShouldNotBeNull();
        second.Topics.Select(t => t.Name).ShouldBe(["topic-0"]);
    }

    // A WebChat conversation's chat id is a 63-bit hash, wider than the double Lua numbers are.
    // The reply stamp patches the record inside Redis, and a stamp that round-trips the id
    // through a double writes noise back over it — after which every read of the topic throws:
    // the sidebar's page, the search hit and the delete all fail on the one poisoned record.
    [Fact]
    public async Task StampingAReply_KeepsAChatIdWiderThanADoubleIntact()
    {
        var store = NewStore();
        const long wideChatId = 6_437_294_812_345_678_901;
        await store.SaveTopicAsync(new TopicMetadata(
            "t-wide-stamp", wideChatId, 0, "agent-wide", "Wide id", DateTimeOffset.UtcNow, null));

        await store.AppendMessagesAsync(
            HistoryKey("agent-wide", wideChatId), [new ChatMessage(ChatRole.Assistant, "a reply")]);

        var topic = (await store.GetTopicPageAsync("agent-wide", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.ChatId.ShouldBe(wideChatId);
        topic.MessageCount.ShouldBe(1);
    }

    // Mark-read is the other in-Redis patch of the same record, so it must carry the id across
    // its round trip the same way.
    [Fact]
    public async Task MarkTopicReadAsync_KeepsAChatIdWiderThanADoubleIntact()
    {
        var store = NewStore();
        const long wideChatId = 8_935_141_660_703_064_219;
        await store.SaveTopicAsync(new TopicMetadata(
            "t-wide-read", wideChatId, 0, "agent-wide-read", "Wide id", DateTimeOffset.UtcNow, null,
            MessageCount: 2));

        await store.MarkTopicReadAsync("agent-wide-read", wideChatId, "t-wide-read");

        var topic = (await store.GetTopicPageAsync("agent-wide-read", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.ChatId.ShouldBe(wideChatId);
        topic.ReadPosition.ShouldBe(2);
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

    // Stamp runs on the Agent host and mark-read on the channel host, against the same record.
    // Whole-record read-modify-write loses one side's fields on the designed flow — mark-read
    // fires while the reply is being stamped — so each writer must patch only its own fields
    // atomically.
    [Fact]
    public async Task MarkTopicReadAsync_RacingTheStampOfAReply_LosesNeitherWrite()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t-race", 650, 0, "agent-race", "Racing", DateTimeOffset.UtcNow, null));

        await Task.WhenAll(Enumerable.Range(0, 100).SelectMany(i => new[]
        {
            store.AppendMessagesAsync(
                HistoryKey("agent-race", 650), [new ChatMessage(ChatRole.Assistant, $"reply {i}")]),
            store.MarkTopicReadAsync("agent-race", 650, "t-race")
        }));

        await store.MarkTopicReadAsync("agent-race", 650, "t-race");

        var topic = (await store.GetTopicPageAsync("agent-race", "default", null, 10)).Topics
            .ShouldHaveSingleItem();
        topic.MessageCount.ShouldBe(100);
        topic.ReadPosition.ShouldBe(100);
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

    // Two conversations written the same millisecond share a score. A cursor of the score alone
    // with an exclusive stop would lose whichever tied row a page break split off — permanently,
    // because paging only ever fetches backwards.
    [Fact]
    public async Task GetTopicPageAsync_TopicsTiedOnTheSameMillisecond_SurviveAPageBoundary()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 7, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        var when = clock.GetUtcNow();
        await store.SaveTopicAsync(new TopicMetadata("t-tied-a", 700, 0, "agent-tied", "tied-a", when, when));
        await store.SaveTopicAsync(new TopicMetadata("t-tied-b", 701, 0, "agent-tied", "tied-b", when, when));
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SaveTopicAsync(new TopicMetadata(
            "t-top", 702, 0, "agent-tied", "top", clock.GetUtcNow(), clock.GetUtcNow()));

        var first = await store.GetTopicPageAsync("agent-tied", "default", null, 2);
        var second = await store.GetTopicPageAsync("agent-tied", "default", first.NextCursor, 2);

        first.Topics.Select(t => t.TopicId).ShouldBe(["t-top", "t-tied-b"]);
        second.Topics.Select(t => t.TopicId).ShouldBe(["t-tied-a"]);
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

    // Reading a conversation is not writing to it. A mark-read that reset the TTL would keep the
    // record and its search document alive months past the history they describe, and search
    // would return a conversation with an empty transcript.
    [Fact]
    public async Task MarkTopicReadAsync_LeavesTheTopicsExpiryWhereItWas()
    {
        var store = NewStore();
        await store.SaveTopicAsync(Topic("t-keep", 680, "agent-keep", DateTimeOffset.UtcNow));
        var db = redisFixture.Connection.GetDatabase();
        await db.KeyExpireAsync("topic:agent-keep:680:t-keep", TimeSpan.FromDays(10));

        await store.MarkTopicReadAsync("agent-keep", 680, "t-keep");

        var ttl = await db.KeyTimeToLiveAsync("topic:agent-keep:680:t-keep");
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromDays(10));
    }

    [Fact]
    public async Task SaveTopicAsync_KeepingTheTtl_DoesNotExtendTheTopicsLife()
    {
        var store = NewStore();
        await store.SaveTopicAsync(Topic("t-rename", 681, "agent-keep", DateTimeOffset.UtcNow));
        var db = redisFixture.Connection.GetDatabase();
        await db.KeyExpireAsync("topic:agent-keep:681:t-rename", TimeSpan.FromDays(10));

        var renamed = (await store.GetTopicAsync("agent-keep", 681, "t-rename"))! with { Name = "Renamed" };
        await store.SaveTopicAsync(renamed, keepTtl: true);

        var ttl = await db.KeyTimeToLiveAsync("topic:agent-keep:681:t-rename");
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromDays(10));
        (await store.GetTopicAsync("agent-keep", 681, "t-rename"))!.Name.ShouldBe("Renamed");
    }

    [Fact]
    public async Task GetTopicAsync_ReturnsWhatWasSaved_AndNullForATopicNeverSaved()
    {
        var store = NewStore();
        var topic = Topic("t-get", 670, "agent-get", DateTimeOffset.UtcNow);
        await store.SaveTopicAsync(topic);

        (await store.GetTopicAsync("agent-get", 670, "t-get")).ShouldBe(topic);
        (await store.GetTopicAsync("agent-get", 670, "t-never")).ShouldBeNull();
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

    // Bypasses SaveTopicAsync on purpose: a topic record that nothing ever put in the index.
    private async Task WriteTopicRecordDirectlyAsync(TopicMetadata topic)
    {
        await redisFixture.Connection.GetDatabase().StringSetAsync(
            $"topic:{topic.AgentId}:{topic.ChatId}:{topic.TopicId}",
            System.Text.Json.JsonSerializer.Serialize(topic));
    }

    private static string HistoryKey(string agentId, long chatId, long threadId = 0) =>
        new AgentKey($"{chatId}:{threadId}", agentId).ToString();
}