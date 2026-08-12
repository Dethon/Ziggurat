using Domain.Agents;
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
    private RedisThreadStateStore NewStore(TimeProvider? time = null) =>
        new(redisFixture.Connection, TimeSpan.FromMinutes(5), time ?? TimeProvider.System);

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
    public async Task GetAllTopicsAsync_FiltersBySpaceSlug()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        await store.SaveTopicAsync(new TopicMetadata("t-s1", 300, 0, "agent-slug", "Space1", now, null,
            SpaceSlug: "space-a"));
        await store.SaveTopicAsync(new TopicMetadata("t-s2", 301, 0, "agent-slug", "Space2", now, null,
            SpaceSlug: "space-b"));

        var filtered = await store.GetAllTopicsAsync("agent-slug", "space-a");

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

        var topic = (await store.GetAllTopicsAsync("agent-stamp")).ShouldHaveSingleItem();
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

        var topics = await store.GetAllTopicsAsync("agent-order");
        topics.Select(t => t.TopicId).ShouldBe(["t-old", "t-new"]);
    }

    private static string HistoryKey(string agentId, long chatId, long threadId = 0) =>
        new AgentKey($"{chatId}:{threadId}", agentId).ToString();
}