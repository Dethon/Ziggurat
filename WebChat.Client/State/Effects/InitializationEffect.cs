using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class InitializationEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionStore _connectionStore;
    private readonly IChatLiveConnection _liveConnection;
    private readonly IChatSessionService _sessionService;
    private readonly IAgentService _agentService;
    private readonly ITopicService _topicService;
    private readonly IConfigService _configService;
    private readonly ILocalStorageService _localStorage;
    private readonly IStreamResumeService _streamResumeService;
    private readonly IPushSubscriptionService _pushNotificationService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IMessagePipeline _pipeline;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<InitializationEffect> _logger;
    private readonly IDisposable _initializeRegistration;
    private readonly IDisposable _selectUserRegistration;
    private readonly IDisposable _setAgentsRegistration;
    private readonly IDisposable _becameLiveAgainSubscription;
    private int _awaitingAgentCatalog;

    public InitializationEffect(
        Dispatcher dispatcher,
        ConnectionStore connectionStore,
        IChatLiveConnection liveConnection,
        IChatSessionService sessionService,
        IAgentService agentService,
        ITopicService topicService,
        IConfigService configService,
        ILocalStorageService localStorage,
        IStreamResumeService streamResumeService,
        IPushSubscriptionService pushNotificationService,
        UserIdentityStore userIdentityStore,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IMessagePipeline pipeline,
        SpaceStore spaceStore,
        ILogger<InitializationEffect> logger)
    {
        _dispatcher = dispatcher;
        _connectionStore = connectionStore;
        _liveConnection = liveConnection;
        _sessionService = sessionService;
        _agentService = agentService;
        _topicService = topicService;
        _configService = configService;
        _localStorage = localStorage;
        _streamResumeService = streamResumeService;
        _pushNotificationService = pushNotificationService;
        _userIdentityStore = userIdentityStore;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _pipeline = pipeline;
        _spaceStore = spaceStore;
        _logger = logger;

        _initializeRegistration = dispatcher.RegisterHandler<Initialize>(
            _ => HandleInitializeAsync().LogFaults(_logger, nameof(Initialize)));
        _selectUserRegistration = dispatcher.RegisterHandler<SelectUser>(
            action => RegisterUserAsync(action.UserId).LogFaults(_logger, nameof(SelectUser)));
        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(
            action => HandleAgentCatalogArrivedAsync(action.Agents).LogFaults(_logger, nameof(SetAgents)));

        // Nothing else retries a not-live catalog fetch on its own: a SetAgents broadcast (the
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
            throw;
        }

        await RegisterUserAsync();

        // Validate and join space (must happen before push subscribe so space context is set)
        var spaceSlug = _spaceStore.State.CurrentSlug;
        var space = await _configService.GetSpaceAsync(spaceSlug);
        if (space is null)
        {
            _dispatcher.Dispatch(new InvalidSpace());
            spaceSlug = _spaceStore.State.CurrentSlug;
            space = await _configService.GetSpaceAsync(spaceSlug);
        }

        if (space is not null)
        {
            await _topicService.JoinSpaceAsync(spaceSlug);
            _dispatcher.Dispatch(new SpaceValidated(spaceSlug, space.Name, space.AccentColor));
        }

        // Best-effort, network/browser-dependent — must not gate the rest of init
        // (agent list, topics). Push subscription still runs after space join so the
        // server can associate it with the space context. A slow pushManager.subscribe()
        // previously stalled the agent list ~30s by being awaited here.
        SubscribePushAsync().LogFaults(_logger, "push subscription");

        var catalog = await _agentService.GetAgentsAsync();
        if (!catalog.IsLive)
        {
            // Nothing else retries this fetch on its own: a SetAgents broadcast (the agent
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

    // The deferred completion is owed exactly once, to whichever path gets here first.
    private bool ClaimAgentCatalog() => Interlocked.Exchange(ref _awaitingAgentCatalog, 0) == 1;

    private async Task SelectAgentAndLoadTopicsAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
        if (agents.Count == 0)
        {
            return;
        }

        var savedAgentId = await _localStorage.GetAsync("selectedAgentId");
        var savedAgent = agents.FirstOrDefault(a => a.Id == savedAgentId);
        var agentToSelect = savedAgent ?? agents[0];
        _dispatcher.Dispatch(new SelectAgent(agentToSelect.Id));

        if (savedAgent is null)
        {
            await _localStorage.SetAsync("selectedAgentId", agentToSelect.Id);
        }

        var firstPage = await _topicService.GetTopicPageAsync(agentToSelect.Id, _spaceStore.State.CurrentSlug);
        if (!firstPage.IsLive)
        {
            return;
        }

        var topics = firstPage.Value!.Topics.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics, firstPage.Value.NextCursor));

        // Gathered rather than detached: awaiting first-load init has to mean history is in
        // the store, or a caller that awaits it still races the messages it asked for.
        await Task.WhenAll(topics.Select(LoadTopicHistoryAsync));
    }

    public Task RegisterUserAsync(string? userId = null)
    {
        userId ??= _userIdentityStore.State.SelectedUserId;
        return string.IsNullOrEmpty(userId) ? Task.CompletedTask : _sessionService.RegisterUserAsync(userId);
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

    private async Task LoadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        if (!history.IsLive)
        {
            return;
        }

        _pipeline.LoadHistory(topic.TopicId, history.Value!);

        // If this topic is currently selected, mark it as read so no stale badges appear
        if (_topicsStore.State.SelectedTopicId == topic.TopicId)
        {
            await MarkTopicAsReadAsync(topic);
        }

        // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean
        // awaiting the conversation.
        _streamResumeService.TryResumeStreamAsync(topic).LogFaults(_logger, "stream resume");
    }

    private Task MarkTopicAsReadAsync(StoredTopic topic) =>
        TopicReadState.MarkReadAsync(topic, _dispatcher, _topicService);

    public void Dispose()
    {
        _initializeRegistration.Dispose();
        _selectUserRegistration.Dispose();
        _setAgentsRegistration.Dispose();
        _becameLiveAgainSubscription.Dispose();
    }
}