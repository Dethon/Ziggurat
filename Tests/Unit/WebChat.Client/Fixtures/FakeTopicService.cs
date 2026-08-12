using System.Globalization;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeTopicService(CallRecorder? recorder = null) : ITopicService
{
    private readonly Dictionary<(long ChatId, long ThreadId), List<ChatHistoryMessage>> _history = new();
    private readonly List<TopicMetadata> _seededTopics = new();
    private readonly List<TopicMetadata> _savedTopics = new();
    private readonly HashSet<string> _deletedTopicIds = new();
    private readonly List<string> _joinedSpaces = new();

    public void SetHistory(long chatId, long threadId, params ChatHistoryMessage[] messages)
    {
        _history[(chatId, threadId)] = messages.ToList();
    }

    public void SetHistory(long chatId, long threadId, List<ChatHistoryMessage> messages)
    {
        _history[(chatId, threadId)] = messages;
    }

    // Topics the server already has. Kept apart from SavedTopics so a test can still assert
    // on what the code under test wrote.
    public FakeTopicService SeedTopic(TopicMetadata topic)
    {
        _seededTopics.Add(topic);
        return this;
    }

    public Exception? ThrowOnGetAllTopics { get; set; }

    public Exception? ThrowOnGetHistory { get; set; }

    public Exception? ThrowOnDeleteTopic { get; set; }

    public Exception? ThrowOnSaveTopic { get; set; }

    // Holds the delete open so a test can interleave user actions with the round trip.
    public TaskCompletionSource? DeleteGate { get; set; }

    public IReadOnlyList<TopicMetadata> SavedTopics => _savedTopics;
    public IReadOnlySet<string> DeletedTopicIds => _deletedTopicIds;
    public IReadOnlyList<string> JoinedSpaces => _joinedSpaces;

    // Set to answer not live for every call, the way a transport between connections does.
    public bool NotLive { get; set; }

    // Answers not live for only the named agents, so a test can prove a sibling agent's
    // successful read survives this one's failure.
    public HashSet<string> NotLiveForAgentIds { get; } = [];

    // How many rows one fetch answers with. Large by default so a test that does not care about
    // paging sees the whole seeded list on the first page.
    public int PageSize { get; set; } = 100;

    public Task<HubResult<TopicPage>> GetTopicPageAsync(
        string agentId, string spaceSlug = SpaceConfig.DefaultSlug, string? cursor = null)
    {
        recorder?.Record(cursor is null ? $"topics:{agentId}" : $"topics:{agentId}:{cursor}");

        if (ThrowOnGetAllTopics is not null)
        {
            return Task.FromException<HubResult<TopicPage>>(ThrowOnGetAllTopics);
        }

        if (NotLive || NotLiveForAgentIds.Contains(agentId))
        {
            return Task.FromResult(HubResult<TopicPage>.NotLive);
        }

        // Ordered and cut the way the server's index is, so a cursor means the same thing here.
        var ordered = _seededTopics.Concat(_savedTopics)
            .Where(t => t.AgentId == agentId && t.SpaceSlug == spaceSlug)
            .GroupBy(t => t.TopicId)
            .Select(g => g.Last())
            .OrderByDescending(t => t.LastMessageAt ?? t.CreatedAt)
            .ToList();

        var below = cursor is null
            ? ordered
            : ordered.Where(t => Score(t) < double.Parse(cursor, CultureInfo.InvariantCulture)).ToList();

        var page = below.Take(PageSize).ToList();
        var next = page.Count < PageSize
            ? null
            : Score(page[^1]).ToString(CultureInfo.InvariantCulture);

        return Task.FromResult(HubResult<TopicPage>.Answered(new TopicPage(
            page, next, [.. page.Select(t => t.TopicId).Where(LiveTopicIds.Contains)])));
    }

    // Which of the seeded topics the server would report as having a reply in flight.
    public HashSet<string> LiveTopicIds { get; } = [];

    // Read positions the fake was told to move, so a test can assert which conversations were
    // marked read without reaching for the seeded records.
    public IReadOnlyList<string> MarkedReadTopicIds => _markedRead;

    private readonly List<string> _markedRead = new();

    public Task<HubResult<Nothing>> MarkTopicReadAsync(string agentId, long chatId, string topicId)
    {
        recorder?.Record($"read:{topicId}");

        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _markedRead.Add(topicId);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    private static double Score(TopicMetadata topic) =>
        (topic.LastMessageAt ?? topic.CreatedAt).ToUnixTimeMilliseconds();

    public Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug)
    {
        recorder?.Record($"join:{spaceSlug}");

        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _joinedSpaces.Add(spaceSlug);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        recorder?.Record($"save:{topic.TopicId}");

        if (ThrowOnSaveTopic is not null)
        {
            return Task.FromException<HubResult<Nothing>>(ThrowOnSaveTopic);
        }

        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _savedTopics.Add(topic);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    public async Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId)
    {
        recorder?.Record($"delete:{topicId}");

        if (DeleteGate is not null)
        {
            await DeleteGate.Task;
        }

        if (ThrowOnDeleteTopic is not null)
        {
            throw ThrowOnDeleteTopic;
        }

        if (NotLive)
        {
            return HubResult<Nothing>.NotLive;
        }

        _deletedTopicIds.Add(topicId);
        return HubResult<Nothing>.Answered(default);
    }

    public Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(
        string agentId, long chatId, long threadId)
    {
        recorder?.Record($"history:{chatId}:{threadId}");

        if (ThrowOnGetHistory is not null)
        {
            return Task.FromException<HubResult<IReadOnlyList<ChatHistoryMessage>>>(ThrowOnGetHistory);
        }

        if (NotLive)
        {
            return Task.FromResult(HubResult<IReadOnlyList<ChatHistoryMessage>>.NotLive);
        }

        return Task.FromResult(HubResult<IReadOnlyList<ChatHistoryMessage>>.Answered(
            _history.TryGetValue((chatId, threadId), out var h) ? h : []));
    }
}