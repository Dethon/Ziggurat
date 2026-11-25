using System.Globalization;
using Domain.DTOs.Household;
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
    private readonly List<HouseholdSpace> _spaces = new();

    // A space of the signed-in person's household, as the hub answers them.
    public FakeTopicService WithSpace(
        string slug, string name = "Main", string accentColor = "#112233", string? defaultAgent = null)
    {
        _spaces.Add(new HouseholdSpace
        {
            Slug = slug, Name = name, AccentColor = accentColor, DefaultAgent = defaultAgent, Order = _spaces.Count
        });
        return this;
    }

    public Exception? ThrowOnGetSpaces { get; set; }

    // What the hub tells the space switcher: every place the person may go.
    public IReadOnlyList<SwitcherEntry> SwitcherEntries { get; set; } = [];

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

    public Exception? ThrowOnDeleteFrom { get; set; }

    // Answers given before the history SetHistory holds, one per read: what a read made too early saw.
    public Queue<List<ChatHistoryMessage>> HistoryAnswers { get; } = new();

    public List<(string AgentId, string TopicId, long ChatId, string EntryId, string? LastSeen)> DeletedFrom { get; } = [];

    public List<(string AgentId, string TopicId, long ChatId, string Slot, int Index, string? LastSeen)> SwitchedVersions { get; } = [];

    public List<(string AgentId, string TopicId, long ChatId, string EntryId, string? LastSeen)> PutAside { get; } = [];

    public Exception? ThrowOnPutAside { get; set; }

    public ForkedTopic? ForkAnswer { get; set; }

    public List<(string AgentId, string TopicId, long ChatId, string EntryId)> Forked { get; } = [];

    public Task<HubResult<ForkedTopic>> ForkAsync(string agentId, string topicId, long chatId, string entryId)
    {
        Forked.Add((agentId, topicId, chatId, entryId));
        return Task.FromResult(NotLive || ForkAnswer is null
            ? HubResult<ForkedTopic>.NotLive
            : HubResult<ForkedTopic>.Answered(ForkAnswer));
    }

    public List<(string AgentId, string TopicId, long ChatId, string UserEntryId, string? LastSeen)> RepliesPutAside { get; } = [];

    public Task<HubResult<Nothing>> PutAsideReplyAsync(
        string agentId, string topicId, long chatId, string userEntryId, string? lastSeen)
    {
        RepliesPutAside.Add((agentId, topicId, chatId, userEntryId, lastSeen));
        return Task.FromResult(NotLive ? HubResult<Nothing>.NotLive : HubResult<Nothing>.Answered(default));
    }

    public List<(string AgentId, string TopicId, long ChatId, string EntryId, string Text, string? LastSeen)> EditedInPlace { get; } = [];

    public Task<HubResult<Nothing>> EditInPlaceAsync(
        string agentId, string topicId, long chatId, string entryId, string text, string? lastSeen)
    {
        EditedInPlace.Add((agentId, topicId, chatId, entryId, text, lastSeen));
        return Task.FromResult(NotLive ? HubResult<Nothing>.NotLive : HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<Nothing>> PutAsideFromAsync(
        string agentId, string topicId, long chatId, string entryId, string? lastSeen)
    {
        if (ThrowOnPutAside is not null)
        {
            return Task.FromException<HubResult<Nothing>>(ThrowOnPutAside);
        }

        PutAside.Add((agentId, topicId, chatId, entryId, lastSeen));
        return Task.FromResult(NotLive ? HubResult<Nothing>.NotLive : HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<Nothing>> SwitchVersionAsync(
        string agentId, string topicId, long chatId, string slot, int index, string? lastSeen)
    {
        SwitchedVersions.Add((agentId, topicId, chatId, slot, index, lastSeen));
        return Task.FromResult(NotLive ? HubResult<Nothing>.NotLive : HubResult<Nothing>.Answered(default));
    }

    public Exception? ThrowOnDeleteTopic { get; set; }

    public Exception? ThrowOnSaveTopic { get; set; }

    // Holds the delete open so a test can interleave user actions with the round trip.
    public TaskCompletionSource? DeleteGate { get; set; }

    // Holds every page fetch open so a test can change the list underneath the round trip.
    public TaskCompletionSource? PageGate { get; set; }

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

    // Topics the fake should answer with only when the archived range is asked for. Archived is
    // a position in the server's index, so the fake carries the two ranges apart rather than
    // deciding which a topic is in.
    public HashSet<string> ArchivedTopicIds { get; } = [];

    public async Task<HubResult<TopicPage>> GetTopicPageAsync(
        string agentId,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        bool archived = false,
        TopicFilter? filter = null)
    {
        recorder?.Record(
            (archived ? "archived" : "topics")
            + (cursor is null ? $":{agentId}" : $":{agentId}:{cursor}")
            + FilterNote(filter));
        Filters.Add(filter);

        if (PageGate is not null)
        {
            await PageGate.Task;
        }

        if (ThrowOnGetAllTopics is not null)
        {
            throw ThrowOnGetAllTopics;
        }

        if (NotLive || NotLiveForAgentIds.Contains(agentId))
        {
            return HubResult<TopicPage>.NotLive;
        }

        // Ordered and cut the way the server's index is, so a cursor means the same thing here.
        var ordered = _seededTopics.Concat(_savedTopics)
            .Where(t => t.AgentId == agentId && t.SpaceSlug == spaceSlug)
            .Where(t => ArchivedTopicIds.Contains(t.TopicId) == archived)
            .Where(t => Admits(filter, t))
            .GroupBy(t => t.TopicId)
            .Select(g => g.Last())
            .OrderByDescending(t => t.LastWriteAt)
            .ToList();

        var below = cursor is null
            ? ordered
            : ordered.Where(t => Score(t) < double.Parse(cursor, CultureInfo.InvariantCulture)).ToList();

        var page = below.Take(PageSize).ToList();
        var next = page.Count < PageSize
            ? null
            : Score(page[^1]).ToString(CultureInfo.InvariantCulture);

        return HubResult<TopicPage>.Answered(new TopicPage(
            page, next, [.. page.Select(t => t.TopicId).Where(LiveTopicIds.Contains)]));
    }

    // Every filter a page or a search was asked with, in order: none is null.
    public List<TopicFilter?> Filters { get; } = [];

    // Who "mine" is to this fake's filter, and whose topics the hub names to whoever sees all.
    public string SignedInUserId { get; set; } = "me";

    public List<HouseholdMemberName> People { get; } = [];

    public Task<HubResult<IReadOnlyList<HouseholdMemberName>>> GetHouseholdPeopleAsync() =>
        Task.FromResult(NotLive
            ? HubResult<IReadOnlyList<HouseholdMemberName>>.NotLive
            : HubResult<IReadOnlyList<HouseholdMemberName>>.Answered([.. People]));

    // The rule the server applies, with the fake's person seeing all; a test that cares about who sees
    // what drives the hub.
    private bool Admits(TopicFilter? filter, TopicMetadata topic) => filter switch
    {
        null or { MineOnly: false, OwnerId: null } => true,
        { MineOnly: true } => topic.OwnerId is null || topic.OwnerId == SignedInUserId || topic.Shared,
        { OwnerId: { } owner } => topic.OwnerId == owner
    };

    // Recorded only when one narrows the list, so a recorder that predates filters reads as it did.
    private static string FilterNote(TopicFilter? filter) => filter switch
    {
        { MineOnly: true } => ":mine",
        { OwnerId: { } owner } => $":of-{owner}",
        _ => ""
    };

    // Which of the seeded topics the server would report as having a reply in flight.
    public HashSet<string> LiveTopicIds { get; } = [];

    // Matches on the name, over both ranges, the way the server's index does — a test that cares
    // about content matching drives the store rather than this.
    public async Task<HubResult<TopicPage>> SearchTopicsAsync(
        string agentId,
        string query,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        TopicFilter? filter = null)
    {
        recorder?.Record((cursor is null ? $"search:{query}" : $"search:{query}:{cursor}") + FilterNote(filter));
        Filters.Add(filter);

        if (PageGate is not null)
        {
            await PageGate.Task;
        }

        if (NotLive)
        {
            return HubResult<TopicPage>.NotLive;
        }

        var ordered = _seededTopics.Concat(_savedTopics)
            .Where(t => t.AgentId == agentId && t.SpaceSlug == spaceSlug)
            .Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(t => Admits(filter, t))
            .GroupBy(t => t.TopicId)
            .Select(g => g.Last())
            .OrderByDescending(t => t.LastWriteAt)
            .ToList();

        var below = cursor is null
            ? ordered
            : ordered.Where(t => Score(t) < double.Parse(cursor, CultureInfo.InvariantCulture)).ToList();

        var page = below.Take(PageSize).ToList();
        return HubResult<TopicPage>.Answered(new TopicPage(
            page,
            page.Count < PageSize ? null : Score(page[^1]).ToString(CultureInfo.InvariantCulture)));
    }

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
        topic.LastWriteAt.ToUnixTimeMilliseconds();

    public Task<HubResult<IReadOnlyList<SwitcherEntry>>> GetSpacesAndAppsAsync()
    {
        recorder?.Record("spaces and apps");
        return Task.FromResult(NotLive
            ? HubResult<IReadOnlyList<SwitcherEntry>>.NotLive
            : HubResult<IReadOnlyList<SwitcherEntry>>.Answered(SwitcherEntries));
    }

    public Task<HubResult<IReadOnlyList<HouseholdSpace>>> GetSpacesAsync()
    {
        recorder?.Record("spaces");

        if (ThrowOnGetSpaces is not null)
        {
            return Task.FromException<HubResult<IReadOnlyList<HouseholdSpace>>>(ThrowOnGetSpaces);
        }

        return Task.FromResult(NotLive
            ? HubResult<IReadOnlyList<HouseholdSpace>>.NotLive
            : HubResult<IReadOnlyList<HouseholdSpace>>.Answered(_spaces.ToList()));
    }

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

    private readonly List<(string AgentId, long ChatId, string TopicId, bool Shared)> _sharedChanges = [];

    public IReadOnlyList<(string AgentId, long ChatId, string TopicId, bool Shared)> SharedChanges => _sharedChanges;

    public Task<HubResult<Nothing>> SetTopicSharedAsync(string agentId, long chatId, string topicId, bool shared)
    {
        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _sharedChanges.Add((agentId, chatId, topicId, shared));
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

    public Task<HubResult<Nothing>> DeleteFromAsync(
        string agentId, string topicId, long chatId, string entryId, string? lastSeen)
    {
        recorder?.Record($"delete-from:{topicId}:{entryId}");
        if (ThrowOnDeleteFrom is not null)
        {
            return Task.FromException<HubResult<Nothing>>(ThrowOnDeleteFrom);
        }

        DeletedFrom.Add((agentId, topicId, chatId, entryId, lastSeen));
        return Task.FromResult(NotLive ? HubResult<Nothing>.NotLive : HubResult<Nothing>.Answered(default));
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

        if (HistoryAnswers.TryDequeue(out var early))
        {
            return Task.FromResult(HubResult<IReadOnlyList<ChatHistoryMessage>>.Answered(early));
        }

        return Task.FromResult(HubResult<IReadOnlyList<ChatHistoryMessage>>.Answered(
            _history.TryGetValue((chatId, threadId), out var h) ? h : []));
    }
}