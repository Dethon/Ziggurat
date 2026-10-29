using System.Reactive.Linq;

namespace WebChat.Client.State.Connection;

public record ConnectionConnecting : IAction;

public record ConnectionConnected : IAction;

public record ConnectionReconnecting : IAction;

public record ConnectionReconnected : IAction;

public record ConnectionClosed(string? Error) : IAction;

// The user was registered again on the connection that became live at this epoch. The hub refuses
// every call from a connection that has not registered, and a connection that comes back is a new
// one to it, so catching up waits for this rather than for the connection alone.
public record ConnectionRegistered(int Epoch) : IAction;

public sealed class ConnectionStore : IDisposable
{
    private readonly Store<ConnectionState> _store;
    private readonly IDisposable _suppressedEpochTracking;
    private bool _suppressNextEpoch = true;
    private int _suppressedEpoch = -1;
    private bool _suppressedEpochDecided;

    public ConnectionStore(Dispatcher dispatcher)
    {
        _store = new Store<ConnectionState>(ConnectionState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));

        // Registered here, ahead of every external BecameLiveAgain subscriber, so the decision
        // is made exactly once and every subscriber agrees on it — instead of each one racing
        // to consume a shared flag off its own copy of the pipeline.
        _suppressedEpochTracking = _store.StateObservable.Subscribe(DecideSuppressedEpoch);
    }

    public ConnectionState State => _store.State;

    public IObservable<ConnectionState> StateObservable => _store.StateObservable;

    // Suppresses recovery for the app's one connect call whose own inline steps already do
    // what recovery exists to do — its first. Defaults to armed, matching the old assumption
    // that the first connect is always that one; a connect that fails before reaching
    // Connected must disarm it, or the epoch a later rebuild produces would be mistaken for
    // the inline call and recovery would silently never run for it.
    public void ArmInlineInitialConnect() => _suppressNextEpoch = true;

    public void DisarmInlineInitialConnect() => _suppressNextEpoch = false;

    private void DecideSuppressedEpoch(ConnectionState state)
    {
        if (_suppressedEpochDecided || state.Status != ConnectionStatus.Connected)
        {
            return;
        }

        _suppressedEpochDecided = true;
        if (_suppressNextEpoch)
        {
            _suppressedEpoch = state.Epoch;
        }
    }

    // Every epoch after the one accounted for above — that is, each interruption a subscriber
    // still has to catch up on. Session recovery is keyed on this, so the rule lives here once
    // instead of in each subscriber.
    public IObservable<int> BecameLiveAgain => _store.StateObservable
        .Where(state => state.Status == ConnectionStatus.Connected)
        .Select(state => state.Epoch)
        .DistinctUntilChanged()
        .Where(epoch => epoch != _suppressedEpoch);

    // The same epochs, each once session recovery has registered the user on it. Anything that
    // asks the hub for something on becoming live again is keyed on this rather than on
    // BecameLiveAgain: the hub refuses a connection that has not registered, and recovery runs
    // beside every other subscriber rather than ahead of them. A registration answered for an
    // epoch the connection has already moved past counts for nothing.
    public IObservable<int> RegisteredAgain => _store.StateObservable
        .Where(state => state.Status == ConnectionStatus.Connected && state.RegisteredEpoch == state.Epoch)
        .Select(state => state.Epoch)
        .DistinctUntilChanged()
        .Where(epoch => epoch != _suppressedEpoch);

    public void Dispose()
    {
        _suppressedEpochTracking.Dispose();
        _store.Dispose();
    }

    private static ConnectionState Reduce(ConnectionState state, IAction action) => action switch
    {
        ConnectionConnecting => state with
        {
            Status = ConnectionStatus.Connecting
        },

        ConnectionConnected => state with
        {
            Status = ConnectionStatus.Connected,
            LastConnected = DateTime.UtcNow,
            ReconnectAttempts = 0,
            Error = null,
            Epoch = state.Epoch + 1
        },

        ConnectionReconnecting => state with
        {
            Status = ConnectionStatus.Reconnecting,
            ReconnectAttempts = state.ReconnectAttempts + 1
        },

        ConnectionReconnected => state with
        {
            Status = ConnectionStatus.Connected,
            LastConnected = DateTime.UtcNow,
            ReconnectAttempts = 0,
            Error = null,
            Epoch = state.Epoch + 1
        },

        ConnectionRegistered a => state with
        {
            RegisteredEpoch = a.Epoch
        },

        ConnectionClosed a => state with
        {
            Status = ConnectionStatus.Disconnected,
            Error = a.Error
        },

        _ => state
    };
}