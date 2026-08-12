using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentSelectionEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IChatSessionService _sessionService;
    private readonly ILocalStorageService _localStorage;
    private readonly ITopicService _topicService;
    private readonly IStreamResumeService _streamResumeService;
    private readonly StreamingStore _streamingStore;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<AgentSelectionEffect> _logger;
    private readonly IDisposable _selectAgentRegistration;
    private readonly IDisposable _setAgentsRegistration;
    private string? _previousAgentId;

    public AgentSelectionEffect(
        TopicsStore topicsStore,
        Dispatcher dispatcher,
        IChatSessionService sessionService,
        ILocalStorageService localStorage,
        ITopicService topicService,
        IStreamResumeService streamResumeService,
        StreamingStore streamingStore,
        SpaceStore spaceStore,
        ILogger<AgentSelectionEffect> logger)
    {
        _dispatcher = dispatcher;
        _sessionService = sessionService;
        _localStorage = localStorage;
        _topicService = topicService;
        _streamResumeService = streamResumeService;
        _streamingStore = streamingStore;
        _spaceStore = spaceStore;
        _logger = logger;

        // Both registrations read the selection after the stores have reduced, which is safe
        // because stores are constructed before effects. SetAgents is registered as well as
        // SelectAgent because the topics reducer clears the selected agent when a refreshed
        // catalog no longer contains it, and the hub dispatches SetAgents whenever the agent
        // re-registers its catalog.
        _selectAgentRegistration = dispatcher.RegisterHandler<SelectAgent>(
            _ => HandleAgentChangedAsync(topicsStore.State.SelectedAgentId)
                .LogFaults(_logger, nameof(SelectAgent)));
        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(
            _ => HandleAgentChangedAsync(topicsStore.State.SelectedAgentId)
                .LogFaults(_logger, nameof(SetAgents)));
    }

    public async Task HandleAgentChangedAsync(string? agentId)
    {
        var previousAgentId = _previousAgentId;
        _previousAgentId = agentId;

        // The first selection belongs to first load, which InitializationEffect already loads
        // topics for; without this guard first load fetches every topic twice.
        if (previousAgentId is null || agentId == previousAgentId)
        {
            return;
        }

        _sessionService.ClearSession();
        await _localStorage.SetAsync("selectedAgentId", agentId ?? "");
        await LoadTopicsForAgentAsync(agentId);
    }

    private async Task LoadTopicsForAgentAsync(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            _dispatcher.Dispatch(new TopicsLoaded([]));
            return;
        }

        var spaceSlug = _spaceStore.State.CurrentSlug;
        var firstPage = await _topicService.GetTopicPageAsync(agentId, spaceSlug);

        // Not live is not an empty list. Storing it as one is what empties the sidebar when a
        // resuming phone switches agents mid-rebuild; the epoch reloads it a moment later.
        if (!firstPage.IsLive)
        {
            return;
        }

        var topics = firstPage.Value!.Topics.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics, firstPage.Value.NextCursor));

        // Gathered rather than detached, so awaiting an agent change means the new agent's
        // history is in the store.
        await Task.WhenAll(topics.Select(LoadTopicHistoryAsync));
    }

    private async Task LoadTopicHistoryAsync(StoredTopic topic)
    {
        // Skip history reload for streaming topics - they have correct local state
        // and reloading would lose locally-added messages not yet persisted to server
        if (_streamingStore.State.StreamingTopics.Contains(topic.TopicId))
        {
            ResumeStream(topic);
            return;
        }

        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        if (!history.IsLive)
        {
            return;
        }

        var messages = history.Value!.Select(h => h.ToChatMessageModel()).ToList();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));

        ResumeStream(topic);
    }

    // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean awaiting
    // the conversation.
    private void ResumeStream(StoredTopic topic) =>
        _streamResumeService.TryResumeStreamAsync(topic).LogFaults(_logger, "stream resume");

    public void Dispose()
    {
        _selectAgentRegistration.Dispose();
        _setAgentsRegistration.Dispose();
    }
}