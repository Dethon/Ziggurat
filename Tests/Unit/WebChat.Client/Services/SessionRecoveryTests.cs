using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Services;
using WebChat.Client.State;
using WebChat.Client.State.Space;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class SessionRecoveryTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly SpaceStore _spaceStore;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly FakeChatSessionService _sessionService;
    private readonly FakeTopicService _topicService;
    private readonly FakePushSubscriptionService _pushService = new();
    private readonly SessionRecovery _recovery;

    public SessionRecoveryTests()
    {
        _spaceStore = new SpaceStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        _sessionService = new FakeChatSessionService(_calls);
        _topicService = new FakeTopicService(_calls);

        _recovery = new SessionRecovery(
            _sessionService, _topicService, _pushService, _userIdentityStore, _spaceStore);
    }

    [Fact]
    public async Task RecoverAsync_ReIdentifiesTheUserRejoinsTheSpaceAndResendsThePushSubscription()
    {
        _dispatcher.Dispatch(new SelectUser("user-1"));
        _dispatcher.Dispatch(new SpaceValidated("other", "Other", "#445566"));
        _calls.Reset();

        await _recovery.RecoverAsync();

        _calls.Calls.ShouldContain("register-user");
        _topicService.JoinedSpaces.ShouldBe(["other"]);
        _pushService.ResubscribeCalls.ShouldBe(1);
    }

    // The hub refuses every call from a connection that has not registered, and a reconnected
    // connection is a new one: rejoining the space before the hub has the user back is refused.
    [Fact]
    public async Task RecoverAsync_RejoinsTheSpaceOnlyOnceTheUserIsRegisteredAgain()
    {
        _dispatcher.Dispatch(new SelectUser("user-1"));
        _sessionService.RegistrationAnswer = new TaskCompletionSource();

        var recovering = _recovery.RecoverAsync();

        _topicService.JoinedSpaces.ShouldBeEmpty();
        _pushService.ResubscribeCalls.ShouldBe(0);

        _sessionService.RegistrationAnswer.SetResult();
        await recovering;

        _topicService.JoinedSpaces.ShouldBe(["default"]);
        _pushService.ResubscribeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RecoverAsync_DoesNotForceRefreshThePushChannel()
    {
        await _recovery.RecoverAsync();

        // A full resubscribe generates a new endpoint in Chrome and loses the space
        // memberships attached to the old one.
        _pushService.SubscribedVapidKey.ShouldBeNull();
        _pushService.ResubscribeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RecoverAsync_PushResubscribeFails_StillRejoinsTheSpace()
    {
        _pushService.ThrowOnResubscribe = new InvalidOperationException("push endpoint gone");

        await Should.NotThrowAsync(() => _recovery.RecoverAsync());

        _topicService.JoinedSpaces.ShouldBe(["default"]);
    }

    [Fact]
    public async Task RecoverAsync_NoSelectedUser_StillRejoinsTheSpace()
    {
        await _recovery.RecoverAsync();

        _calls.Calls.ShouldNotContain("register-user");
        _topicService.JoinedSpaces.ShouldBe(["default"]);
    }

    public void Dispose()
    {
        _spaceStore.Dispose();
        _userIdentityStore.Dispose();
    }
}