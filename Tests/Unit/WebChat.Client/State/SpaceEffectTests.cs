using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class SpaceEffectTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly SpaceStore _spaceStore;
    private readonly ConnectionStore _connectionStore;
    private readonly ToastStore _toastStore;
    private readonly FakeTopicService _topicService;
    private readonly FakeNavigationManager _navigationManager = new();
    private readonly FakePushSubscriptionService _pushService = new();
    private readonly RecordingLogger<SpaceEffect> _logger = new();
    private readonly SpaceEffect _effect;

    public SpaceEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _connectionStore = new ConnectionStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _dispatcher.Dispatch(new ConnectionConnected());
        _topicService = new FakeTopicService(_calls);

        _effect = new SpaceEffect(
            _dispatcher,
            _topicService,
            _connectionStore,
            _navigationManager,
            _pushService,
            _logger);
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_KnownSpace_JoinsItAndMovesThePushSubscription()
    {
        _topicService.WithSpace("other", name: "Other", accentColor: "#445566");
        GivenLoadedTopicsAndMessages();

        await _effect.HandleSelectSpaceAsync("other");

        _topicService.JoinedSpaces.ShouldBe(["other"]);
        _pushService.ResubscribeCalls.ShouldBe(1);
        _spaceStore.State.CurrentSlug.ShouldBe("other");
        _spaceStore.State.SpaceName.ShouldBe("Other");
        _topicsStore.State.Topics.ShouldBeEmpty();
        _messagesStore.State.MessagesByTopic.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_UnknownSpaceWhileConnected_DoesNotJoinAndGoesHome()
    {
        await _effect.HandleSelectSpaceAsync("ghost");

        _topicService.JoinedSpaces.ShouldBeEmpty();
        _navigationManager.NavigatedTo.ShouldBe(["/"]);
        _spaceStore.State.CurrentSlug.ShouldBe("default");
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_UnknownSpaceWhileDisconnected_LeavesInitialisationToDoIt()
    {
        _dispatcher.Dispatch(new ConnectionClosed(null));

        await _effect.HandleSelectSpaceAsync("ghost");

        _topicService.JoinedSpaces.ShouldBeEmpty();
        _navigationManager.NavigatedTo.ShouldBeEmpty();
    }

    // Switching space is the user's own action, so a join that could not be made says so
    // (ADR-0004) rather than clearing the sidebar for a space the server never put us in.
    [Fact]
    public async Task HandleSelectSpaceAsync_JoinThatCouldNotBeMade_RaisesOneToastAndKeepsTheSidebar()
    {
        _topicService.WithSpace("other", name: "Other", accentColor: "#445566");
        GivenLoadedTopicsAndMessages();
        _topicService.NotLive = true;

        await _effect.HandleSelectSpaceAsync("other");

        _toastStore.State.Toasts.Count.ShouldBe(1);
        _topicsStore.State.Topics.ShouldNotBeEmpty();
        _messagesStore.State.MessagesByTopic.ShouldNotBeEmpty();
        _spaceStore.State.SpaceName.ShouldBe(SpaceState.Initial.SpaceName);
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_AfterAJoinThatCouldNotBeMade_TheNextAttemptRetriesIt()
    {
        _topicService.WithSpace("other", name: "Other");
        _topicService.NotLive = true;
        await _effect.HandleSelectSpaceAsync("other");

        _topicService.NotLive = false;
        await _effect.HandleSelectSpaceAsync("other");

        _topicService.JoinedSpaces.ShouldBe(["other"]);
        _spaceStore.State.SpaceName.ShouldBe("Other");
    }

    // Before the hub is up this is the first navigation, not a switch: InitializationEffect
    // joins the slug the reducer already stored and validates it, so there is nothing to say.
    [Fact]
    public async Task HandleSelectSpaceAsync_KnownSpaceWhileDisconnected_LeavesInitialisationToDoItSilently()
    {
        _topicService.WithSpace("other", name: "Other");
        _dispatcher.Dispatch(new ConnectionClosed(null));

        await _effect.HandleSelectSpaceAsync("other");

        _topicService.JoinedSpaces.ShouldBeEmpty();
        _toastStore.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_SameSlugAsBefore_DoesNothing()
    {
        _topicService.WithSpace("default");

        await _effect.HandleSelectSpaceAsync("default");

        _calls.Calls.ShouldBeEmpty();
        _topicService.JoinedSpaces.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleSelectSpaceAsync_PushResubscribeFails_StillFinishesTheTransition()
    {
        _topicService.WithSpace("other", name: "Other");
        _pushService.ThrowOnResubscribe = new InvalidOperationException("no push channel");

        await _effect.HandleSelectSpaceAsync("other");

        _spaceStore.State.SpaceName.ShouldBe("Other");
    }

    [Fact]
    public async Task Dispatch_SelectSpace_RunsTheSameWork()
    {
        _topicService.WithSpace("other", name: "Other");

        _dispatcher.Dispatch(new SelectSpace("other"));

        await TestChat.Eventually(() => _spaceStore.State.SpaceName == "Other");
        _topicService.JoinedSpaces.ShouldBe(["other"]);
    }

    [Fact]
    public async Task Dispatch_SelectSpace_FaultIsLoggedRatherThanDiscarded()
    {
        _topicService.ThrowOnGetSpaces = new InvalidOperationException("space lookup failed");

        _dispatcher.Dispatch(new SelectSpace("other"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("space lookup failed");
    }

    // First load validates a space the address bar does not name - the slug was a space the
    // household removed, so the household's first space opened instead. The address follows,
    // or a reload or a shared link names the removed space again.
    [Fact]
    public void ASpaceValidatedThatTheAddressDoesNotName_ReplacesTheAddressWithItsOwn()
    {
        var navigation = new FakeNavigationManager("http://localhost/cocina");
        using var effect = new SpaceEffect(_dispatcher, _topicService, _connectionStore, navigation, _pushService, _logger);

        _dispatcher.Dispatch(new SpaceValidated("default", "Main", "#112233"));

        navigation.NavigatedTo.ShouldBe(["/"]);
    }

    [Fact]
    public void ASpaceValidatedThatTheAddressAlreadyNames_LeavesTheAddress()
    {
        var navigation = new FakeNavigationManager("http://localhost/kitchen?x=1");
        using var effect = new SpaceEffect(_dispatcher, _topicService, _connectionStore, navigation, _pushService, _logger);

        _dispatcher.Dispatch(new SpaceValidated("kitchen", "Kitchen", "#112233"));

        navigation.NavigatedTo.ShouldBeEmpty();
    }

    // The corrected address renders the page again, which selects the space it now names: that
    // is the space first load already joined, so it is not a switch to make again.
    [Fact]
    public async Task TheSelectionTheCorrectedAddressMakes_IsNotASwitch()
    {
        _topicService.WithSpace("kitchen", name: "Kitchen");
        _dispatcher.Dispatch(new ConnectionClosed(null));
        await _effect.HandleSelectSpaceAsync("cocina");
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new SpaceValidated("kitchen", "Kitchen", "#112233"));

        await _effect.HandleSelectSpaceAsync("kitchen");

        _topicService.JoinedSpaces.ShouldNotContain("kitchen");
    }

    private void GivenLoadedTopicsAndMessages()
    {
        var topic = new StoredTopic
        { TopicId = "topic-1", ChatId = 10, ThreadId = 20, AgentId = "agent-1", Name = "Topic" };

        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [
            new ChatMessageModel { Role = "assistant", Content = "hello", MessageId = "m-1" }
        ]));
        _calls.Reset();
    }

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _spaceStore.Dispose();
        _connectionStore.Dispose();
        _toastStore.Dispose();
    }
}