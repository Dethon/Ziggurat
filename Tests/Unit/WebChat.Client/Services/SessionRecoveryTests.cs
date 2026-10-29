using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Services;
using WebChat.Client.State;
using WebChat.Client.State.Space;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class SessionRecoveryTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly SpaceStore _spaceStore;
    private readonly FakeTopicService _topicService;
    private readonly FakePushSubscriptionService _pushService = new();
    private readonly SessionRecovery _recovery;

    public SessionRecoveryTests()
    {
        _spaceStore = new SpaceStore(_dispatcher);
        _topicService = new FakeTopicService(_calls);

        _recovery = new SessionRecovery(_topicService, _pushService, _spaceStore);
    }

    // The connection that came back is its person already — the hub took them from the proxy's
    // headers as it arrived — so recovery has nobody to name and asks for the space at once.
    [Fact]
    public async Task RecoverAsync_RejoinsTheSpaceAndResendsThePushSubscription()
    {
        _dispatcher.Dispatch(new SpaceValidated("other", "Other", "#445566"));
        _calls.Reset();

        await _recovery.RecoverAsync();

        _topicService.JoinedSpaces.ShouldBe(["other"]);
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

    public void Dispose()
    {
        _spaceStore.Dispose();
    }
}