using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class InitializationEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionStore _connectionStore;
    private readonly IChatLiveConnection _liveConnection;
    private readonly IAgentService _agentService;
    private readonly ITopicService _topicService;
    private readonly IConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private readonly IStreamResumeService _streamResumeService;
    private readonly IPushSubscriptionService _pushNotificationService;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<InitializationEffect> _logger;
    private readonly IDisposable _initializeRegistration;
    private readonly IDisposable _setAgentsRegistration;
    private readonly IDisposable _agentsUpdatedRegistration;
    private readonly IDisposable _becameLiveAgainSubscription;
    private int _awaitingAgentCatalog;
    private int _awaitingFirstConnect;

    public InitializationEffect(
        Dispatcher dispatcher,
        ConnectionStore connectionStore,
        IChatLiveConnection liveConnection,
        IAgentService agentService,
        ITopicService topicService,
        IConfigService configService,
        ILocalStorageService localStorage,
        IStreamResumeService streamResumeService,
        IPushSubscriptionService pushNotificationService,
        SpaceStore spaceStore,
        ILogger<InitializationEffect> logger)
    {
        _dispatcher = dispatcher;
        _connectionStore = connectionStore;
        _liveConnection = liveConnection;
        _agentService = agentService;
        _topicService = topicService;
        _configService = configService;
        _localStorage = localStorage;
        _streamResumeService = streamResumeService;
        _pushNotificationService = pushNotificationService;
        _spaceStore = spaceStore;
        _logger = logger;

        _initializeRegistration = dispatcher.RegisterHandler<Initialize>(
            _ => HandleInitializeAsync().LogFaults(_logger, nameof(Initialize)));
        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(
            action => HandleAgentCatalogArrivedAsync(action.Agents).LogFaults(_logger, nameof(SetAgents)));
        _agentsUpdatedRegistration = dispatcher.RegisterHandler<AgentsUpdated>(
            _ => RefetchAgentCatalogAsync().LogFaults(_logger, nameof(AgentsUpdated)));

        // Nothing else retries a not-live catalog fetch on its own: an AgentsUpdated push (the
        // agent re-registering) is one way it completes, and the next connection epoch is the
        // other — the client's own retry, not dependent on the agent process doing anything.
        _becameLiveAgainSubscription = connectionStore.BecameLiveAgain.Subscribe(
            _ => RetryAgentCatalogIfAwaitingAsync().LogFaults(_logger, nameof(ConnectionStore.BecameLiveAgain)));
    }

    public async Task HandleInitializeAsync()
    {
        // Armed before the call: this connect's own inline steps below already do what session
        // recovery exists to do, so its epoch must not trigger recovery too. A connect that
        // never reaches Connected disarms it, so whichever epoch a later rebuild produces is
        // not mistaken for this one and recovery runs for it instead.
        _connectionStore.ArmInlineInitialConnect();
        try
        {
            await _liveConnection.ConnectAsync();
        }
        catch
        {
            _connectionStore.DisarmInlineInitialConnect();
            // The connection keeps trying by itself, and whichever epoch makes it live first is this
            // page's first: everything below is owed to it.
            Volatile.Write(ref _awaitingFirstConnect, 1);
            throw;
        }

        await InitializeConnectedAsync();
    }

    // Everything a first load does once it is connected, whether that was its own connect or, after
    // that failed, the first epoch the connection reached by itself.
    private async Task InitializeConnectedAsync()
    {
        // Nothing to say about who this is: the hub took the person from the proxy's headers as
        // the connection arrived, so everything below may be asked at once.

        // Validate and join space (must happen before push subscribe so space context is set). The
        // household's spaces are the hub's to say; a slug that is none of them opens the household's
        // first space instead.
        var spaces = await _topicService.GetSpacesAsync();
        if (spaces is { IsLive: true, Value: { } household }
            && (household.FirstOrDefault(s => s.Slug == _spaceStore.State.CurrentSlug) ?? household.FirstOrDefault()) is { } space)
        {
            await _topicService.JoinSpaceAsync(space.Slug);
            _dispatcher.Dispatch(new SpaceValidated(space.Slug, space.Name, space.AccentColor, space.DefaultAgent));
        }

        // Best-effort, network/browser-dependent — must not gate the rest of init
        // (agent list, topics). Push subscription still runs after space join so the
        // server can associate it with the space context. A slow pushManager.subscribe()
        // previously stalled the agent list ~30s by being awaited here.
        SubscribePushAsync().LogFaults(_logger, "push subscription");

        var catalog = await _agentService.GetAgentsAsync();
        if (!catalog.IsLive)
        {
            // Nothing else retries this fetch on its own: an AgentsUpdated push (the agent
            // re-registering) or the next connection epoch (BecameLiveAgain, below) completes
            // the initialization this load could not.
            Volatile.Write(ref _awaitingAgentCatalog, 1);
            return;
        }

        _dispatcher.Dispatch(new SetAgents(catalog.Value!));
        await SelectAgentAndLoadTopicsAsync(catalog.Value!);
    }

    // A catalog arriving after a first load that could not fetch it. The reducer has already stored
    // the agents; what first load never got to do — pick the agent and load its topics — happens
    // here, exactly once.
    private async Task HandleAgentCatalogArrivedAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
        if (agents.Count == 0 || !ClaimAgentCatalog())
        {
            return;
        }

        await SelectAgentAndLoadTopicsAsync(agents);
    }

    // The client's own retry for a not-live first load: becoming live again is not dependent
    // on the agent process re-registering, so this fires whether or not that ever happens. A
    // retry that also answers not live leaves the flag set for the epoch after this one.
    private async Task RetryAgentCatalogIfAwaitingAsync()
    {
        if (Interlocked.Exchange(ref _awaitingFirstConnect, 0) == 1)
        {
            await InitializeConnectedAsync();
            return;
        }

        if (Volatile.Read(ref _awaitingAgentCatalog) == 0)
        {
            return;
        }

        var catalog = await _agentService.GetAgentsAsync();

        // The same reconnect also carries the agent's re-registration broadcast, so the catalog
        // may have arrived that way while this fetch was in flight. Claiming after the await is
        // what keeps the two from both completing initialization — the second run would fetch
        // every topic again and re-select the agent, dropping the conversation on screen.
        if (!catalog.IsLive || !ClaimAgentCatalog())
        {
            return;
        }

        _dispatcher.Dispatch(new SetAgents(catalog.Value!));
        await SelectAgentAndLoadTopicsAsync(catalog.Value!);
    }

    // The agent registered again: this household's agents are asked for afresh, and their arrival is
    // a SetAgents like any other, so it also finishes a first load that could not fetch them.
    private async Task RefetchAgentCatalogAsync()
    {
        if (await _agentService.GetAgentsAsync() is { IsLive: true, Value: { } agents })
        {
            _dispatcher.Dispatch(new SetAgents(agents));
        }
    }

    // The deferred completion is owed exactly once, to whichever path gets here first.
    private bool ClaimAgentCatalog() => Interlocked.Exchange(ref _awaitingAgentCatalog, 0) == 1;

    private async Task SelectAgentAndLoadTopicsAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
        if (agents.Count == 0)
        {
            return;
        }

        // Not remembered: what the space opened on is the person's pick only once they pick it, so a
        // default the household changes later still reaches them.
        var agentToSelect = await SpaceAgent.ChooseAsync(_localStorage, agents, _spaceStore.State);
        _dispatcher.Dispatch(new SelectAgent(agentToSelect.Id, Picked: false));

        var firstPage = await _topicService.GetTopicPageAsync(agentToSelect.Id, _spaceStore.State.CurrentSlug);
        if (!firstPage.IsLive)
        {
            return;
        }

        var topics = firstPage.Value!.Topics.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics, firstPage.Value.NextCursor));

        // No history and no per-topic sweep. Opening the client costs one page of rows and
        // nothing per conversation: the badge and the preview a row needs are on the row, the
        // transcript is fetched when a conversation is opened, and the page itself says which
        // replies are in flight.
        TopicPageStreams.ResumeReported(topics, firstPage.Value.LiveTopicIds, _streamResumeService, _logger);
    }

    private async Task SubscribePushAsync()
    {
        try
        {
            var config = await _configService.GetConfigAsync();
            if (!string.IsNullOrEmpty(config.VapidPublicKey))
            {
                await _pushNotificationService.RequestAndSubscribeAsync(config.VapidPublicKey);
            }
        }
        catch
        {
            // Push subscription is best-effort — don't block the app
        }
    }


    public void Dispose()
    {
        _initializeRegistration.Dispose();
        _setAgentsRegistration.Dispose();
        _agentsUpdatedRegistration.Dispose();
        _becameLiveAgainSubscription.Dispose();
    }
}