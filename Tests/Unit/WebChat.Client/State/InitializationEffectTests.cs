using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class InitializationEffectTests : IDisposable
{
    private static readonly AgentCatalogEntry _agentOne = new("agent-1", "Agent One", null);
    private static readonly AgentCatalogEntry _agentTwo = new("agent-2", "Agent Two", null);

    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly ConnectionStore _connectionStore;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly SpaceStore _spaceStore;
    private readonly FakeChatLiveConnection _liveConnection;
    private readonly FakeAgentService _agentService;
    private readonly FakeTopicService _topicService;
    private readonly FakeConfigService _configService;
    private readonly FakeLocalStorageService _localStorage;
    private readonly FakeStreamResumeService _streamResumeService;
    private readonly FakePushSubscriptionService _pushService;
    private readonly RecordingLogger<InitializationEffect> _logger = new();
    private readonly InitializationEffect _effect;
    private readonly ArmedClock _time = new();

    public InitializationEffectTests()
    {
        _connectionStore = new ConnectionStore(_dispatcher);
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);

        _liveConnection = new FakeChatLiveConnection(_calls);
        _agentService = new FakeAgentService(_calls);
        _topicService = new FakeTopicService(_calls);
        _configService = new FakeConfigService(_calls);
        _localStorage = new FakeLocalStorageService(_calls);
        _streamResumeService = new FakeStreamResumeService();
        _pushService = new FakePushSubscriptionService();

        _effect = new InitializationEffect(
            _dispatcher,
            _connectionStore,
            _liveConnection,
            _agentService,
            _topicService,
            _configService,
            _localStorage,
            _streamResumeService,
            _pushService,
            _spaceStore,
            _logger,
            _time);
    }

    [Fact]
    public async Task HandleInitializeAsync_FirstLoad_RunsTheStepsInOrder()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _calls.Reset();

        await _effect.HandleInitializeAsync();

        // Nothing between the connect and the rest: the hub took the person from the proxy's
        // headers as the connection arrived, so there is nobody to name and nothing to wait for.
        _calls.Calls.ShouldBe(
        [
            "connect",
            "spaces",
            "join:default",
            "agents",
            // No agentConfigPatch read here: per-agent settings hang off SetAgents, which
            // AgentSettingsEffect handles, so they load on every catalog rather than this one.
            "storage-get:selectedAgentId",
            "topics:agent-1"
            // No history: opening the client costs one page of rows and nothing per
            // conversation. A transcript is fetched when its conversation is opened.
        ]);
    }

    [Fact]
    // The inverse of what this used to assert. Loading every conversation on start-up is the
    // cost the whole of this work removes; the rows carry their own badge and preview, so the
    // sidebar is complete without a single message.
    public async Task HandleInitializeAsync_Completes_NoTopicsHistoryIsInTheStore()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _topicService.SeedTopic(TestChat.Topic("topic-2", chatId: 11, threadId: 21));
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "first"));
        _topicService.SetHistory(11, 21, TestChat.HistoryMessage("m-2", "second"));

        await _effect.HandleInitializeAsync();

        _messagesStore.State.MessagesByTopic.ShouldBeEmpty();
        _calls.Calls.ShouldNotContain(call => call.StartsWith("history:"));
    }

    [Fact]
    public async Task HandleInitializeAsync_PushSubscriptionHangs_StillFinishesTheRest()
    {
        _topicService.WithSpace("default");
        _configService.Config = new AppConfig(null, "vapid-public-key");
        _pushService.BlockUntilReleased = true;
        _agentService.Agents = [_agentOne];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-1");
        await _pushService.SubscribeCalled.WaitAsync(TimeSpan.FromSeconds(5));
        _pushService.SubscribedVapidKey.ShouldBe("vapid-public-key");
    }

    [Fact]
    public async Task HandleInitializeAsync_StreamResumeHangs_StillFinishesTheRest()
    {
        _topicService.WithSpace("default");
        _streamResumeService.BlockUntilReleased = true;
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _topicService.LiveTopicIds.Add("topic-1");

        await _effect.HandleInitializeAsync();

        _topicsStore.State.Topics.Select(t => t.TopicId).ShouldBe(["topic-1"]);
        _streamResumeService.ResumedTopicIds.ShouldBe(["topic-1"]);
    }

    [Fact]
    public async Task HandleInitializeAsync_ASlugThatIsNoneOfTheHouseholdsSpaces_OpensItsFirstSpace()
    {
        _topicService.WithSpace("kitchen", name: "Kitchen").WithSpace("default", name: "Main");
        _dispatcher.Dispatch(new SelectSpace("ghost"));
        _calls.Reset();

        await _effect.HandleInitializeAsync();

        _calls.Calls.ShouldBe(["connect", "spaces", "join:kitchen", "agents"]);
        _spaceStore.State.CurrentSlug.ShouldBe("kitchen");
        _spaceStore.State.SpaceName.ShouldBe("Kitchen");
    }

    // The household names an agent for the space, and nobody picked another in it.
    [Fact]
    public async Task HandleInitializeAsync_ASpaceWithADefaultAgent_OpensOnIt()
    {
        _topicService.WithSpace("default").WithSpace("kitchen", name: "Kitchen", defaultAgent: "agent-2");
        _dispatcher.Dispatch(new SelectSpace("kitchen"));
        _agentService.Agents = [_agentOne, _agentTwo];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-2");
        _calls.Calls.ShouldContain("topics:agent-2");
    }

    // A space that names no agent, on a device that never picked one, opens on the agent the
    // household answers by default - not on whichever it lists first, which in the home is Jack, the
    // download assistant, while the Agents page says Jonas answers by default.
    [Fact]
    public async Task HandleInitializeAsync_ASpaceNamingNoAgent_OpensOnTheHouseholdsDefault()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne, _agentTwo with { IsHouseholdDefault = true }];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-2");
    }

    [Fact]
    public async Task HandleInitializeAsync_APickInTheSpace_WinsOverItsDefaultAgent()
    {
        _topicService.WithSpace("kitchen", name: "Kitchen", defaultAgent: "agent-2");
        _dispatcher.Dispatch(new SelectSpace("kitchen"));
        _localStorage.Seed("selectedAgentId:kitchen", "agent-1");
        _agentService.Agents = [_agentOne, _agentTwo];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-1");
    }

    // A default agent the household no longer runs is no agent to open on.
    [Fact]
    public async Task HandleInitializeAsync_ASpacesDefaultAgentIsNotInTheCatalog_OpensOnTheFirst()
    {
        _topicService.WithSpace("default", defaultAgent: "agent-gone");
        _agentService.Agents = [_agentOne, _agentTwo];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-1");
    }

    [Fact]
    public async Task HandleInitializeAsync_UnknownSpaceWithNoFallback_DoesNotJoin()
    {
        _dispatcher.Dispatch(new SelectSpace("ghost"));

        await _effect.HandleInitializeAsync();

        _topicService.JoinedSpaces.ShouldBeEmpty();
    }

    // Opening the app in the not-live window: the catalog fetch answers not live and first load
    // stops before selecting an agent. The catalog still arrives — the agent re-registers and the
    // hub broadcasts SetAgents — and that arrival has to finish what first load could not, or the
    // sidebar stays empty until a manual reload.
    [Fact]
    public async Task HandleInitializeAsync_TheCatalogWasNotLive_ALaterSetAgentsCompletesInitialization()
    {
        _topicService.WithSpace("default");
        _agentService.NotLive = true;
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "first"));
        await _effect.HandleInitializeAsync();
        _topicsStore.State.SelectedAgentId.ShouldBeNull();

        _dispatcher.Dispatch(new SetAgents([_agentOne]));

        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 1);
    }

    // The agent registering again tells a browser only that agents may have changed, since every
    // household is shown its own: the client asks for its household's again and shows that answer.
    [Fact]
    public async Task AgentsUpdated_AsksForTheHouseholdsAgentsAgain()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        await _effect.HandleInitializeAsync();

        _agentService.Agents = [_agentOne, _agentTwo];
        _dispatcher.Dispatch(new AgentsUpdated());

        await TestChat.Eventually(() => _topicsStore.State.Agents.Count == 2);
    }

    // Opened in the not-live window, the nudge finishes what first load could not, as the list it
    // once carried did.
    [Fact]
    public async Task HandleInitializeAsync_TheCatalogWasNotLive_ALaterAgentsUpdatedCompletesInitialization()
    {
        _topicService.WithSpace("default");
        _agentService.NotLive = true;
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        await _effect.HandleInitializeAsync();

        _agentService.NotLive = false;
        _agentService.Agents = [_agentOne];
        _dispatcher.Dispatch(new AgentsUpdated());

        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 1);
    }

    // Nothing broadcasts SetAgents on its own — the agent has to re-register for that. The
    // client cannot wait on that; the next connection epoch has to retry the fetch itself, or
    // the sidebar stays empty until a manual reload.
    [Fact]
    public async Task HandleInitializeAsync_TheCatalogWasNotLive_TheNextEpochRetriesAndCompletesInitialization()
    {
        _topicService.WithSpace("default");
        _agentService.NotLive = true;
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _topicService.SetHistory(10, 20, TestChat.HistoryMessage("m-1", "first"));
        await _effect.HandleInitializeAsync();
        _dispatcher.Dispatch(new ConnectionConnected());
        _topicsStore.State.SelectedAgentId.ShouldBeNull();

        _agentService.NotLive = false;
        _agentService.Agents = [_agentOne];
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 1);
    }

    // A page opened while the hub restarts (a deploy) fails its first connect, and the connection
    // keeps trying on its own. Its first epoch is the first live one this page ever had: the spaces,
    // the agents and the topics are all still owed, not only a catalog. It used to come up Connected
    // with no agents and stay that way.
    [Fact]
    public async Task HandleInitializeAsync_TheFirstConnectFailed_TheFirstEpochFinishesInitialization()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        _liveConnection.ThrowOnConnect = new HttpRequestException("502 Bad Gateway");
        await Should.ThrowAsync<HttpRequestException>(() => _effect.HandleInitializeAsync());

        _liveConnection.ThrowOnConnect = null;
        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 1);
        _topicService.JoinedSpaces.ShouldBe(["default"]);
    }

    // A retry that also comes up not live must not clear the awaiting flag — the epoch after
    // that one is still owed a retry.
    [Fact]
    public async Task HandleInitializeAsync_TheRetryIsAlsoNotLive_TheEpochAfterThatStillRetries()
    {
        _topicService.WithSpace("default");
        _agentService.NotLive = true;
        await _effect.HandleInitializeAsync();
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _dispatcher.Dispatch(new ConnectionConnected());
        _topicsStore.State.SelectedAgentId.ShouldBeNull();

        _agentService.NotLive = false;
        _agentService.Agents = [_agentOne];
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
    }

    // A reconnect both retries the catalog fetch and carries the agent's re-registration
    // broadcast with it. Only one of the two may finish the deferred initialization: running it
    // twice fetches every topic again and re-selects the agent, which drops the conversation the
    // user is reading.
    [Fact]
    public async Task HandleInitializeAsync_ARetryAndABroadcastOnTheSameEpoch_CompleteInitializationOnce()
    {
        _topicService.WithSpace("default");
        _agentService.NotLive = true;
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        await _effect.HandleInitializeAsync();
        _dispatcher.Dispatch(new ConnectionConnected());

        _agentService.NotLive = false;
        _agentService.Agents = [_agentOne];
        _agentService.Gate = new TaskCompletionSource();
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new SetAgents([_agentOne]));
        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
        _dispatcher.Dispatch(new SelectTopic("topic-1"));
        _calls.Reset();

        _agentService.Gate.SetResult();

        await Task.Delay(50);
        _calls.Calls.ShouldNotContain(call => call.StartsWith("topics:"));
        _topicsStore.State.SelectedTopicId.ShouldBe("topic-1");
    }

    // The deferred completion belongs to a first load that could not fetch the catalog. A first
    // load that did fetch it has already selected and loaded, so the broadcasts that follow must
    // not load everything again.
    [Fact]
    public async Task HandleInitializeAsync_TheCatalogWasLive_ALaterSetAgentsLoadsNothingAgain()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _topicService.SeedTopic(TestChat.Topic("topic-1"));
        await _effect.HandleInitializeAsync();
        _calls.Reset();

        _dispatcher.Dispatch(new SetAgents([_agentOne]));

        await Task.Delay(50);
        _calls.Calls.ShouldNotContain(call => call.StartsWith("topics:"));
    }

    [Fact]
    public async Task HandleInitializeAsync_NoAgents_SelectsNothingAndLoadsNoTopics()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBeNull();
        _calls.Calls.ShouldNotContain(call => call.StartsWith("topics:"));
    }

    [Fact]
    // What the space opened on is not the person's pick until they pick it, so a default the
    // household sets later still reaches them.
    public async Task HandleInitializeAsync_SavedAgentIsGone_FallsBackToTheFirstWithoutRememberingIt()
    {
        _topicService.WithSpace("default");
        _localStorage.Seed("selectedAgentId", "agent-gone");
        _agentService.Agents = [_agentOne, _agentTwo];

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-1");
        _localStorage.Values["selectedAgentId"].ShouldBe("agent-gone");
    }

    [Fact]
    public async Task HandleInitializeAsync_SavedAgentIsInTheCatalog_SelectsItWithoutRewriting()
    {
        _topicService.WithSpace("default");
        _localStorage.Seed("selectedAgentId", "agent-2");
        _agentService.Agents = [_agentOne, _agentTwo];
        _calls.Reset();

        await _effect.HandleInitializeAsync();

        _topicsStore.State.SelectedAgentId.ShouldBe("agent-2");
        _calls.Calls.ShouldNotContain("storage-set:selectedAgentId");
    }

    [Fact]
    public async Task Dispatch_Initialize_RunsTheSameWork()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];

        _dispatcher.Dispatch(new Initialize());

        await TestChat.Eventually(() => _topicsStore.State.SelectedAgentId == "agent-1");
        _liveConnection.ConnectCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatch_Initialize_FaultIsLoggedRatherThanDiscarded()
    {
        _topicService.WithSpace("default");
        _agentService.ThrowOnGetAgents = new InvalidOperationException("agent list unavailable");

        _dispatcher.Dispatch(new Initialize());

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("agent list unavailable");
    }

    // The hub answered, but could not read the household just then (the accounts service slow, a
    // session check refused for a moment): the connection stays live, so nothing else would ever
    // finish the load — a person was left on an empty, "Connected" page with no agent and no topic
    // until they reloaded. The first load asks again, later, until it is answered.
    [Fact]
    public async Task HandleInitializeAsync_TheHubCouldNotReadTheHousehold_AsksAgainUntilItCan()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _agentService.ThrowOnGetAgents = new Microsoft.AspNetCore.SignalR.HubException("This household's agents could not be read right now.");
        _agentService.ThrowTimes = 2;

        var loading = _effect.HandleInitializeAsync();
        await _time.AdvancePastAsync(InitializationEffect.FirstRetryDelay);
        await _time.AdvancePastAsync(InitializationEffect.FirstRetryDelay * 2);
        await loading;

        _calls.Calls.Count(c => c == "agents").ShouldBe(3);
        _calls.Calls.ShouldContain("topics:agent-1");
    }

    [Fact]
    public async Task Disposed_StopsHandlingInitialize()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _effect.Dispose();

        _dispatcher.Dispatch(new Initialize());

        await Task.Delay(50);
        _liveConnection.ConnectCalls.ShouldBe(0);
    }

    // The connect HandleInitializeAsync awaits directly can throw (offline at first load). The
    // epoch that later becomes live — via a rebuild the live connection runs on its own — is
    // then not the one the inline steps below already covered, so recovery has to run for it
    // or nobody ever registers the user or joins the space.
    [Fact]
    public async Task HandleInitializeAsync_InitialConnectThrows_ALaterEpochStillTriggersRecovery()
    {
        _topicService.WithSpace("default");
        _agentService.Agents = [_agentOne];
        _liveConnection.ThrowOnConnect = new InvalidOperationException("offline at first load");
        var recovery = new FakeSessionRecovery();
        using var sessionRecoveryEffect = new SessionRecoveryEffect(
            _connectionStore, recovery, new RecordingLogger<SessionRecoveryEffect>());

        await Should.ThrowAsync<InvalidOperationException>(() => _effect.HandleInitializeAsync());

        // Simulates the live connection's own rebuild reaching Connected on its own, the way
        // ChatLiveConnection.ReconnectIfNeededAsync does outside of HandleInitializeAsync.
        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => recovery.RecoverCalls == 1);
    }

    public void Dispose()
    {
        _pushService.Release();
        _streamResumeService.Release();
        _effect.Dispose();
        _connectionStore.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _spaceStore.Dispose();
    }
}