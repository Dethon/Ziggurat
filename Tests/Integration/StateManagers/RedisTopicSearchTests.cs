using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

// Search runs on the server so it covers conversations the client never loaded, which is the
// whole reason the sidebar's own filter over loaded rows had to go: once the list is paged, that
// filter can only ever find the first page.
[Trait("Category", "Integration")]
public class RedisTopicSearchTests(TopicSearchFixture redis) : IClassFixture<TopicSearchFixture>
{
    // Every test's own agent id, so classes sharing this database do not answer each other's
    // searches.
    private readonly string _agentId = $"agent-search-{Guid.NewGuid():N}";

    private RedisThreadStateStore NewStore(TimeProvider? time = null, TimeSpan? archiveHorizon = null) =>
        new(redis.Connection,
            new RetentionSettings
            {
                PurgeHorizon = TimeSpan.FromDays(365),
                ArchiveHorizon = archiveHorizon ?? TimeSpan.FromDays(182)
            },
            time ?? TimeProvider.System);

    [Fact]
    public async Task SearchTopicsAsync_MatchesTheTopicName()
    {
        var store = NewStore();
        await store.SaveTopicAsync(Topic("t-1", 700, "Planting the tomatoes"));
        await store.SaveTopicAsync(Topic("t-2", 701, "Rewiring the shed"));

        var found = await store.SearchTopicsAsync(_agentId, "default", "tomatoes", null, 10);

        found.Topics.Select(t => t.TopicId).ShouldBe(["t-1"]);
    }

    // The way into the archive: once six months of conversations are behind a filter, remembering
    // the title is not how anyone finds them.
    [Fact]
    public async Task SearchTopicsAsync_SpansTheOrdinaryAndArchivedRanges()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock, archiveHorizon: TimeSpan.FromDays(30));
        await store.SaveTopicAsync(Topic("t-old", 710, "Ancient greenhouse notes", clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromDays(31));
        await store.SaveTopicAsync(Topic("t-new", 711, "Fresh greenhouse notes", clock.GetUtcNow()));

        (await store.GetTopicPageAsync(_agentId, "default", null, 10))
            .Topics.Select(t => t.TopicId).ShouldBe(["t-new"]);

        var found = await store.SearchTopicsAsync(_agentId, "default", "greenhouse", null, 10);

        found.Topics.Select(t => t.TopicId).ShouldBe(["t-new", "t-old"]);
    }

    [Fact]
    public async Task SearchTopicsAsync_PagesLikeAnyOtherListOfTopics()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(clock);
        foreach (var i in Enumerable.Range(0, 3))
        {
            await store.SaveTopicAsync(Topic($"t-page-{i}", 720 + i, $"Kettle number {i}", clock.GetUtcNow()));
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var first = await store.SearchTopicsAsync(_agentId, "default", "kettle", null, 2);
        var second = await store.SearchTopicsAsync(_agentId, "default", "kettle", first.NextCursor, 2);

        first.Topics.Select(t => t.TopicId).ShouldBe(["t-page-2", "t-page-1"]);
        second.Topics.Select(t => t.TopicId).ShouldBe(["t-page-0"]);
    }

    [Fact]
    public async Task SearchTopicsAsync_AnotherSpacesConversation_IsNotFound()
    {
        var store = NewStore();
        await store.SaveTopicAsync(Topic("t-here", 730, "Marmalade recipe") with { SpaceSlug = "kitchen" });

        var found = await store.SearchTopicsAsync(_agentId, "default", "marmalade", null, 10);

        found.Topics.ShouldBeEmpty();
    }

    // A conversation found by something said in it rather than by what it is called.
    [Fact]
    public async Task SearchTopicsAsync_MatchesWhatWasSaidInTheConversation()
    {
        var store = NewStore();
        await store.SaveTopicAsync(Topic("t-said", 740, "Scheduled task"));
        await store.AppendMessagesAsync(HistoryKey(740),
        [
            new ChatMessage(ChatRole.User, "remind me about the dentist on Thursday"),
            new ChatMessage(ChatRole.Assistant, "noted")
        ]);

        var found = await store.SearchTopicsAsync(_agentId, "default", "dentist", null, 10);

        found.Topics.Select(t => t.TopicId).ShouldBe(["t-said"]);
    }

    [Fact]
    public async Task MigrateTopicsAsync_BuildsDocumentsForTopicsThatAlreadyExist()
    {
        var store = NewStore();
        var when = new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);
        await redis.Connection.GetDatabase().StringSetAsync(
            $"topic:{_agentId}:750:t-existing",
            System.Text.Json.JsonSerializer.Serialize(Topic("t-existing", 750, "Old conversation", when)));
        await store.AppendMessagesAsync(HistoryKey(750),
            [new ChatMessage(ChatRole.User, "something about aubergines")]);

        await store.MigrateTopicsAsync();

        var found = await store.SearchTopicsAsync(_agentId, "default", "aubergines", null, 10);

        found.Topics.Select(t => t.TopicId).ShouldBe(["t-existing"]);
    }

    // Purge takes what a topic is searched by with it, on the same clock and refreshed by the
    // same write, so nothing is left findable after the conversation is gone.
    [Fact]
    public async Task DeleteTopicAsync_TakesWhatItWasSearchedByWithIt()
    {
        var store = NewStore();
        await store.SaveTopicAsync(Topic("t-gone", 760, "Rhubarb crumble"));

        await store.DeleteTopicAsync(_agentId, 760, "t-gone");

        (await store.SearchTopicsAsync(_agentId, "default", "rhubarb", null, 10)).Topics.ShouldBeEmpty();
    }

    private TopicMetadata Topic(string topicId, long chatId, string name, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        return new TopicMetadata(topicId, chatId, 0, _agentId, name, when, when);
    }

    private string HistoryKey(long chatId) =>
        new AgentKey($"{chatId}:0", _agentId).ToString();
}