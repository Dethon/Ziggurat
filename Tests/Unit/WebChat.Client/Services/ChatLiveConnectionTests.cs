using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class ChatLiveConnectionTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeHubConnectionFactory _factory = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly ConnectionStore _connectionStore;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly HubEventBinder _binder;
    private readonly ChatLiveConnection _liveConnection;

    public ChatLiveConnectionTests()
    {
        _connectionStore = new ConnectionStore(_dispatcher);
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);

        var topicStreams = new TopicStreams(_dispatcher, _messagesStore);
        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, topicStreams, NullLogger<MessagePipeline>.Instance);
        var hubEventDispatcher = new HubEventDispatcher(
            _dispatcher, _topicsStore, topicStreams, pipeline, new UserIdentityStore(_dispatcher));

        _binder = new HubEventBinder(hubEventDispatcher);
        _liveConnection = new ChatLiveConnection(
            _factory,
            _binder,
            new ConnectionEventDispatcher(_dispatcher),
            _timeProvider);
    }

    [Fact]
    public async Task ConnectAsync_FirstConnect_AServerPushReachesTheStore()
    {
        await _liveConnection.ConnectAsync();

        _factory.Created.Single().Raise("OnTopicChanged", TopicCreated("topic-1"));

        _topicsStore.State.Topics.ShouldContain(topic => topic.TopicId == "topic-1");
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_AfterRebuild_AServerPushStillReachesTheStore()
    {
        await _liveConnection.ConnectAsync();
        _factory.Created.Single().State = HubConnectionState.Reconnecting;

        await _liveConnection.ReconnectIfNeededAsync();
        _factory.Created.Last().Raise("OnTopicChanged", TopicCreated("topic-1"));

        _topicsStore.State.Topics.ShouldContain(topic => topic.TopicId == "topic-1");
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_AfterRebuild_APushOnTheTornDownConnectionChangesNothing()
    {
        await _liveConnection.ConnectAsync();
        var tornDown = _factory.Created.Single();
        tornDown.State = HubConnectionState.Reconnecting;

        await _liveConnection.ReconnectIfNeededAsync();
        tornDown.Raise("OnTopicChanged", TopicCreated("topic-1"));

        _topicsStore.State.Topics.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_APushOnTheDisposedConnectionChangesNothing()
    {
        await _liveConnection.ConnectAsync();
        var connection = _factory.Created.Single();

        await _liveConnection.DisposeAsync();
        connection.Raise("OnTopicChanged", TopicCreated("topic-1"));

        _topicsStore.State.Topics.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_AClosedCallbackOnTheDisposedConnection_LeavesTheStatusAlone()
    {
        await _liveConnection.ConnectAsync();
        var connection = _factory.Created.Single();

        await _liveConnection.DisposeAsync();
        await AdvanceUntilCompleteAsync(connection.RaiseClosedAsync(null));

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task ConnectAsync_TheLiveConnectionReconnects_TheStoreFollowsBothEvents()
    {
        await _liveConnection.ConnectAsync();
        var connection = _factory.Created.Single();

        await connection.RaiseReconnectingAsync(new IOException("transport dropped"));
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Reconnecting);

        await connection.RaiseReconnectedAsync("connection-2");

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
        _connectionStore.State.Epoch.ShouldBe(2);
    }

    // A connection torn down while its own reconnect was still in flight can still raise these
    // two. Left attached, a stale Reconnected advances the epoch — which is what session
    // recovery and catch-up are keyed on — over a transport that is already dead, and a stale
    // Reconnecting flips the UI over a live socket.
    [Fact]
    public async Task ReconnectIfNeededAsync_AfterRebuild_ReconnectEventsOnTheTornDownConnectionChangeNothing()
    {
        await _liveConnection.ConnectAsync();
        var tornDown = _factory.Created.Single();
        tornDown.State = HubConnectionState.Reconnecting;
        await _liveConnection.ReconnectIfNeededAsync();
        var epochAfterRebuild = _connectionStore.State.Epoch;

        await tornDown.RaiseReconnectingAsync(new IOException("stale"));
        await tornDown.RaiseReconnectedAsync("stale-connection");

        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
        _connectionStore.State.Epoch.ShouldBe(epochAfterRebuild);
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_StuckReconnectingConnection_ReplacesItWithFreshConnection()
    {
        await _liveConnection.ConnectAsync();
        var stuck = _factory.Created.Single();
        stuck.State = HubConnectionState.Reconnecting;

        await _liveConnection.ReconnectIfNeededAsync();

        stuck.Disposed.ShouldBeTrue();
        _factory.Created.Count.ShouldBe(2);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_AfterRebuild_AdvancesTheEpochWithoutADisconnectedStatus()
    {
        await _liveConnection.ConnectAsync();
        var statuses = new List<ConnectionStatus>();
        using var subscription = _connectionStore.StateObservable.Subscribe(state => statuses.Add(state.Status));
        _factory.Created.Single().State = HubConnectionState.Reconnecting;

        await _liveConnection.ReconnectIfNeededAsync();

        _connectionStore.State.Epoch.ShouldBe(2);
        statuses.ShouldNotContain(ConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_HungConnectAttempt_IsCancelledAtAttemptTimeoutAndRetried()
    {
        await _liveConnection.ConnectAsync();
        _factory.Created.Single().State = HubConnectionState.Reconnecting;
        var hung = new FakeHubConnection { StartBehavior = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        _factory.Enqueue(hung);

        var reconnect = _liveConnection.ReconnectIfNeededAsync();

        // Just short of the 2.5s per-attempt timeout the hung attempt must still be running.
        _timeProvider.Advance(TimeSpan.FromSeconds(2.4));
        await SettleAsync();
        reconnect.IsCompleted.ShouldBeFalse();
        _factory.Created.Count.ShouldBe(2);

        await AdvanceUntilCompleteAsync(reconnect);

        hung.Disposed.ShouldBeTrue();
        _factory.Created.Count.ShouldBe(3);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_ServerUnreachable_GivesUpDisconnectedAfterFourAttempts()
    {
        await _liveConnection.ConnectAsync();
        _factory.Created.Single().State = HubConnectionState.Reconnecting;
        _factory.CreateBehavior = () => new FakeHubConnection
        {
            StartBehavior = _ => Task.FromException(new IOException("connection refused"))
        };

        var reconnect = _liveConnection.ReconnectIfNeededAsync();
        await AdvanceUntilCompleteAsync(reconnect);

        _factory.Created.Count.ShouldBe(5); // the original connection + 4 rebuild attempts
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Disconnected);
    }

    // A first connect that never reaches Connected used to leave the failed connection in
    // place, bound and with its handlers attached, and the next trigger found it non-null and
    // returned. Nothing then retried until the tab was re-foregrounded into a rebuild.
    [Fact]
    public async Task ConnectAsync_FirstConnectFails_TheNextTriggerConnectsFromScratch()
    {
        _factory.Enqueue(new FakeHubConnection
        {
            StartBehavior = _ => Task.FromException(new IOException("connection refused"))
        });

        await Should.ThrowAsync<IOException>(() => _liveConnection.ConnectAsync());
        await _liveConnection.ConnectAsync();

        _factory.Created.Count.ShouldBe(2);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task ConnectAsync_FirstConnectFails_DropsTheFailedConnectionThere()
    {
        var failed = new FakeHubConnection
        {
            StartBehavior = _ => Task.FromException(new IOException("connection refused"))
        };
        _factory.Enqueue(failed);

        await Should.ThrowAsync<IOException>(() => _liveConnection.ConnectAsync());

        failed.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_AfterAFailedFirstConnect_Recovers()
    {
        _factory.Enqueue(new FakeHubConnection
        {
            StartBehavior = _ => Task.FromException(new IOException("connection refused"))
        });
        await Should.ThrowAsync<IOException>(() => _liveConnection.ConnectAsync());

        await AdvanceUntilCompleteAsync(_liveConnection.ReconnectIfNeededAsync());

        _factory.Created.Count.ShouldBe(2);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_ConnectedAndPingSucceeds_KeepsConnection()
    {
        await _liveConnection.ConnectAsync();
        var live = _factory.Created.Single();

        await _liveConnection.ReconnectIfNeededAsync();

        live.Disposed.ShouldBeFalse();
        _factory.Created.Count.ShouldBe(1);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    [Fact]
    public async Task ReconnectIfNeededAsync_ConnectedButPingFails_Rebuilds()
    {
        await _liveConnection.ConnectAsync();
        var zombie = _factory.Created.Single();
        zombie.PingBehavior = _ => Task.FromException<bool>(new IOException("transport dead"));

        await _liveConnection.ReconnectIfNeededAsync();

        zombie.Disposed.ShouldBeTrue();
        _factory.Created.Count.ShouldBe(2);
        _connectionStore.State.Status.ShouldBe(ConnectionStatus.Connected);
    }

    private static TopicChangedNotification TopicCreated(string topicId) =>
        new(TopicChangeType.Created, topicId, TestChat.Topic(topicId));

    private async Task AdvanceUntilCompleteAsync(Task task)
    {
        foreach (var _ in Enumerable.Range(0, 20).TakeWhile(_ => !task.IsCompleted))
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
            await SettleAsync();
        }

        task.IsCompleted.ShouldBeTrue();
        await task;
    }

    // Real (not fake) delay so continuations queued on the thread pool by a time
    // advance get a chance to run before the next assertion or advance.
    private static Task SettleAsync() => Task.Delay(50);

    public void Dispose()
    {
        _connectionStore.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
    }
}