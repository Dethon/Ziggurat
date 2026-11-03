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
    private readonly IDisposable _spaceValidatedRegistration;
    private readonly IDisposable _reloadTopicsRegistration;
    private readonly TopicsStore _topicsStore;
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
        _topicsStore = topicsStore;
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
            action => HandleAgentChangedAsync(topicsStore.State.SelectedAgentId, action.Picked)
                .LogFaults(_logger, nameof(SelectAgent)));
        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(
            _ => HandleAgentChangedAsync(topicsStore.State.SelectedAgentId)
                .LogFaults(_logger, nameof(SetAgents)));

        // A space opened mid-session opens on its own agent, as first load opens the first space on
        // its own; before the catalog is known that is first load's to do.
        _spaceValidatedRegistration = dispatcher.RegisterHandler<SpaceValidated>(
            _ => OpenSpaceOnItsAgentAsync().LogFaults(_logger, nameof(SpaceValidated)));
        _reloadTopicsRegistration = dispatcher.RegisterHandler<ReloadTopics>(
            _ => LoadTopicsForAgentAsync(topicsStore.State.SelectedAgentId).LogFaults(_logger, nameof(ReloadTopics)));
    }

    private async Task OpenSpaceOnItsAgentAsync()
    {
        var agents = _topicsStore.State.Agents;
        if (agents.Count == 0 || _topicsStore.State.SelectedAgentId is null)
        {
            return;
        }

        var agent = await SpaceAgent.ChooseAsync(_localStorage, agents, _spaceStore.State);
        if (agent.Id != _topicsStore.State.SelectedAgentId)
        {
            _dispatcher.Dispatch(new SelectAgent(agent.Id, Picked: false));
            return;
        }

        // The same agent in another space is another list of conversations.
        await LoadTopicsForAgentAsync(agent.Id);
    }

    public async Task HandleAgentChangedAsync(string? agentId, bool picked = true)
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
        if (picked)
        {
            await _localStorage.SetAsync(SpaceAgent.PickKeyOf(_spaceStore.State.CurrentSlug), agentId ?? "");
        }

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
        // fetched when its conversation is opened, and the page says which of its replies are
        // in flight rather than being asked about each in turn.
        TopicPageStreams.ResumeReported(topics, firstPage.Value.LiveTopicIds, _streamResumeService, _logger);
    }

    public void Dispose()
    {
        _selectAgentRegistration.Dispose();
        _setAgentsRegistration.Dispose();
        _spaceValidatedRegistration.Dispose();
        _reloadTopicsRegistration.Dispose();
    }
}