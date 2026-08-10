using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.Connection;

namespace Tests.Unit.WebChat.Client.State;

public class ConnectionStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionStore _store;

    public ConnectionStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new ConnectionStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Initial_IsDisconnectedWithNothingRecorded()
    {
        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.LastConnected.ShouldBeNull();
        _store.State.ReconnectAttempts.ShouldBe(0);
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void Connecting_SetsStatusConnecting()
    {
        _dispatcher.Dispatch(new ConnectionConnecting());

        _store.State.Status.ShouldBe(ConnectionStatus.Connecting);
    }

    [Fact]
    public void Connecting_LeavesAPreviousErrorInPlace()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _dispatcher.Dispatch(new ConnectionConnecting());

        _store.State.Error.ShouldBe("hub dropped");
    }

    [Fact]
    public void Connected_SetsStatusRecordsTheTimeAndClearsTheError()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        var before = DateTime.UtcNow;

        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.Error.ShouldBeNull();
        _store.State.LastConnected.ShouldNotBeNull();
        _store.State.LastConnected.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void Connected_ResetsTheReconnectAttempts()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.ReconnectAttempts.ShouldBe(0);
    }

    [Fact]
    public void Reconnecting_SetsStatusAndCountsTheAttempt()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.Status.ShouldBe(ConnectionStatus.Reconnecting);
        _store.State.ReconnectAttempts.ShouldBe(1);
    }

    [Fact]
    public void Reconnecting_CountsEveryAttempt()
    {
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.ReconnectAttempts.ShouldBe(3);
    }

    [Fact]
    public void Reconnecting_LeavesAPreviousErrorInPlace()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.Error.ShouldBe("hub dropped");
    }

    [Fact]
    public void Reconnected_SetsStatusRecordsTheTimeAndClearsTheError()
    {
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _dispatcher.Dispatch(new ConnectionReconnecting());
        var before = DateTime.UtcNow;

        _dispatcher.Dispatch(new ConnectionReconnected());

        _store.State.Status.ShouldBe(ConnectionStatus.Connected);
        _store.State.Error.ShouldBeNull();
        _store.State.ReconnectAttempts.ShouldBe(0);
        _store.State.LastConnected.ShouldNotBeNull();
        _store.State.LastConnected.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void Closed_SetsStatusDisconnectedAndKeepsTheError()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));

        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.Error.ShouldBe("hub dropped");
    }

    [Fact]
    public void Closed_WithoutAnError_LeavesTheErrorNull()
    {
        _dispatcher.Dispatch(new ConnectionClosed(null));

        _store.State.Status.ShouldBe(ConnectionStatus.Disconnected);
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void Closed_KeepsTheLastConnectedTimeAndTheReconnectAttempts()
    {
        _dispatcher.Dispatch(new ConnectionConnected());
        var connectedAt = _store.State.LastConnected;
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));

        _store.State.LastConnected.ShouldBe(connectedAt);
        _store.State.ReconnectAttempts.ShouldBe(1);
    }

    [Fact]
    public void Connected_AdvancesTheEpoch()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _store.State.Epoch.ShouldBe(1);
    }

    [Fact]
    public void Reconnected_AdvancesTheEpoch()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionReconnected());

        _store.State.Epoch.ShouldBe(2);
    }

    [Fact]
    public void Connecting_DoesNotAdvanceTheEpoch()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionConnecting());

        _store.State.Epoch.ShouldBe(1);
    }

    [Fact]
    public void Reconnecting_DoesNotAdvanceTheEpoch()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionReconnecting());

        _store.State.Epoch.ShouldBe(1);
    }

    [Fact]
    public void Closed_DoesNotAdvanceTheEpoch()
    {
        _dispatcher.Dispatch(new ConnectionConnected());

        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));

        _store.State.Epoch.ShouldBe(1);
    }

    // The default: nobody has said the first connect is handled inline, so BecameLiveAgain
    // keeps its old behaviour of treating the first live epoch as already accounted for.
    [Fact]
    public async Task BecameLiveAgain_NeverDisarmed_StillSkipsTheFirstEpoch()
    {
        var epochs = new List<int>();
        using var subscription = _store.BecameLiveAgain.Subscribe(epochs.Add);

        _store.ArmInlineInitialConnect();
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => epochs.Count == 1);
        epochs.ShouldBe([2]);
    }

    // The armed inline connect never reached Connected, so whichever epoch becomes live first
    // — even epoch 1 — is a rebuild recovery has to run for, not the inline connect it was
    // armed for.
    [Fact]
    public async Task BecameLiveAgain_ArmedInlineConnectThatFails_DoesNotSkipTheNextEpoch()
    {
        var epochs = new List<int>();
        using var subscription = _store.BecameLiveAgain.Subscribe(epochs.Add);

        _store.ArmInlineInitialConnect();
        _store.DisarmInlineInitialConnect();
        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => epochs.Count == 1);
        epochs.ShouldBe([1]);
    }

    // Only the very first Connected the store ever sees is a candidate for suppression — a
    // disarm that arrives after that decision is already made must not retroactively unskip it.
    [Fact]
    public async Task BecameLiveAgain_DisarmedAfterTheFirstEpoch_DoesNotUnskipIt()
    {
        var epochs = new List<int>();
        using var subscription = _store.BecameLiveAgain.Subscribe(epochs.Add);

        _store.ArmInlineInitialConnect();
        _dispatcher.Dispatch(new ConnectionConnected());
        _store.DisarmInlineInitialConnect();
        _dispatcher.Dispatch(new ConnectionClosed(null));
        _dispatcher.Dispatch(new ConnectionConnected());

        await TestChat.Eventually(() => epochs.Count == 1);
        epochs.ShouldBe([2]);
    }
}