using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentSelectionEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IChatSessionService _sessionService;
    private readonly ILocalStorageService _localStorage;
    private readonly ITopicService _topicService;
    private readonly IStreamResumeService _streamResumeService;
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
        SpaceStore spaceStore,
        ILogger<AgentSelectionEffect> logger)
    {
        _dispatcher = dispatcher;
        _sessionService = sessionService;
        _localStorage = localStorage;
        _topicService = topicService;
        _streamResumeService = streamResumeService;
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

        // Switching agent costs a page of rows and nothing per conversation. A transcript is
        // fetched when its conversation is opened; a reply in flight is still resumed, because
        // it has to reach whoever is watching for it.
        topics.ForEach(ResumeStream);
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