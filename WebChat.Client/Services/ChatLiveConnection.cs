using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class ChatLiveConnection(
    IHubConnectionFactory connectionFactory,
    IHubEventBinder eventBinder,
    ConnectionEventDispatcher connectionEventDispatcher,
    TimeProvider timeProvider) : IChatLiveConnection
{
    private const int MaxRebuildAttempts = 4;
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan _closedRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _rebuildAttemptTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan _rebuildRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly ConnectionEventDispatcher _connectionEventDispatcher = connectionEventDispatcher;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private IChatHubConnection? _connection;
    private bool _disposed;

    // The one place that decides whether a hub call can be made. A connection that is null,
    // still connecting or reconnecting cannot carry one — the last two are present and not
    // live, which is the window the old per-call null guards missed entirely. The transport
    // can also die between the state check and the answer, so the outcome of the call is part
    // of the same decision: a call the transport failed to carry answers not live too.
    private IChatHubConnection? LiveHubConnection =>
        _connection is { State: HubConnectionState.Connected } connection ? connection : null;

    public async Task<HubResult<T>> InvokeAsync<T>(string methodName, params object?[] args)
    {
        if (LiveHubConnection is not { } connection)
        {
            return HubResult<T>.NotLive;
        }

        try
        {
            return await connection.InvokeAsync<T>(methodName, args);
        }
        catch (Exception exception) when (IsTransportFault(exception))
        {
            return HubResult<T>.NotLive;
        }
    }

    public async Task<HubResult<Nothing>> InvokeAsync(string methodName, params object?[] args)
    {
        if (LiveHubConnection is not { } connection)
        {
            return HubResult<Nothing>.NotLive;
        }

        try
        {
            return await connection.InvokeAsync(methodName, args);
        }
        catch (Exception exception) when (IsTransportFault(exception))
        {
            return HubResult<Nothing>.NotLive;
        }
    }

    public async Task<HubResult<IAsyncEnumerable<T>>> StreamAsync<T>(string methodName, params object?[] args)
    {
        if (LiveHubConnection is not { } connection)
        {
            return HubResult<IAsyncEnumerable<T>>.NotLive;
        }

        try
        {
            return await connection.StreamAsync<T>(methodName, args);
        }
        catch (Exception exception) when (IsTransportFault(exception))
        {
            return HubResult<IAsyncEnumerable<T>>.NotLive;
        }
    }

    // The transport-fault family, named: an invocation in flight when the connection dies
    // faults with whatever closed it — a cancellation on a clean close, the socket, WebSocket
    // or IO error otherwise, an HTTP or timeout failure from the outer layers, and an
    // ObjectDisposedException from a connection torn down mid-call. These verbs carry no
    // caller token, so a cancellation surfacing here is never the caller's own. A call that
    // races the state check gets SignalR's own InvalidOperationException for it — that exact
    // message is the one signal that a live check just lost the race, so only that message (or
    // ObjectDisposedException, which derives from it) folds an InvalidOperationException into
    // this family. Anything else — a HubException, which is the server answering, or a
    // client-side serialization or argument bug — is not "not live": it propagates to the
    // caller's fault logging instead of raising a connectivity toast over a programming error.
    private static bool IsTransportFault(Exception exception) => exception switch
    {
        ObjectDisposedException => true,
        InvalidOperationException e => IsConnectionInactiveMessage(e.Message),
        OperationCanceledException or HttpRequestException or TimeoutException or IOException
            or SocketException or WebSocketException => true,
        _ => false
    };

    // SignalR's own wording for a call that reached the transport after it stopped being live:
    // "The '{methodName}' method cannot be called if the connection is not active".
    private static bool IsConnectionInactiveMessage(string message) =>
        message.Contains("connection is not active", StringComparison.OrdinalIgnoreCase);

    public Task ConnectAsync() => StartLiveConnectionAsync(CancellationToken.None);

    private async Task StartLiveConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        var connection = await connectionFactory.CreateAsync();
        _connection = connection;

        // Bind before starting: a push that arrives immediately after the handshake would
        // otherwise land on a connection with no handlers. Binding here is also what makes a
        // rebuilt connection heard at all — the server pushes belong to the hub connection
        // instance, so a rebuild that skipped this step would leave the client connected and deaf.
        eventBinder.Bind(connection);

        connection.Closed += OnConnectionClosed;
        connection.Reconnecting += OnConnectionReconnecting;
        connection.Reconnected += OnConnectionReconnected;

        _connectionEventDispatcher.HandleConnecting();

        try
        {
            await connection.StartAsync(cancellationToken);
        }
        catch
        {
            // A connection that never started is not a connection: left in place it is bound,
            // has its handlers attached, will never auto-reconnect, and makes the next attempt
            // return early because it found one. Drop it here so the next trigger — a rebuild
            // or another connect — starts from scratch, and let the caller see the failure.
            await TearDownAsync();
            throw;
        }

        // The live connection may have been disposed while StartAsync was in flight (e.g. the circuit
        // tore down mid-rebuild). Don't publish state or fire recovery into a dead store —
        // drop the just-started connection instead of leaking it.
        if (_disposed)
        {
            await TearDownAsync();
            return;
        }

        // Publishing Connected advances the connection epoch, which is what session recovery
        // and catch-up are keyed on. Neither runs on the first one.
        _connectionEventDispatcher.HandleConnected();
    }

    // A trigger — a resume, a close, connectivity returning — rather than a request. Another
    // reconnect already running is the work this one wanted done, so it leaves it to that one.
    public Task ReconnectIfNeededAsync() => ReconnectAsync(waitForOneInFlight: false);

    // Opening a file picker on a phone backgrounds the page, and one held open past the server's
    // client timeout kills the connection while the person is still choosing. Their file arrives
    // with the resume, racing the reconnect the resume itself triggered — so a caller carrying it
    // waits for that reconnect to finish rather than being told the connection is down by the very
    // rebuild that is about to bring it back. It goes through the same probe-or-rebuild as a
    // resume, which is also what settles the other half of a picker's cost: the connection this
    // caller is about to use may be a zombie that still reports Connected.
    public async Task<bool> EnsureLiveAsync()
    {
        await ReconnectAsync(waitForOneInFlight: true);
        return LiveHubConnection is not null;
    }

    private async Task ReconnectAsync(bool waitForOneInFlight)
    {
        if (_disposed)
        {
            return;
        }

        if (waitForOneInFlight)
        {
            await _reconnectLock.WaitAsync();
        }
        else if (!await _reconnectLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            var action = ForegroundReconnectPolicy.Decide(_connection?.State);

            // A reported-Connected connection may be a post-background zombie: the transport
            // is dead but no close event fired, so SignalR still thinks it's up. Verify with a
            // quick round-trip before trusting it. A live connection answers in tens of ms; we
            // only spend the full probe timeout on one that is genuinely dead.
            if (action == ForegroundAction.Probe && await IsConnectionLiveAsync())
            {
                return;
            }

            await RebuildAsync();
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task<bool> IsConnectionLiveAsync()
    {
        var connection = _connection;
        if (connection is null)
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(_probeTimeout, timeProvider);
            return await connection.PingAsync(cts.Token);
        }
        catch
        {
            // Timeout, transport failure, or a server without the Ping method — treat the
            // connection as dead and let the caller rebuild it.
            return false;
        }
    }

    private Task OnConnectionReconnecting(Exception? exception)
    {
        _connectionEventDispatcher.HandleReconnecting();
        return Task.CompletedTask;
    }

    private Task OnConnectionReconnected(string? connectionId)
    {
        _connectionEventDispatcher.HandleReconnected();
        return Task.CompletedTask;
    }

    private async Task OnConnectionClosed(Exception? exception)
    {
        _connectionEventDispatcher.HandleClosed(exception);

        // On mobile, the browser suspends JS when backgrounded, so SignalR's automatic
        // reconnect can't run. When the app resumes the transport may be dead and queued
        // retries fail at once, firing Closed. Wait briefly then rebuild from scratch.
        await Task.Delay(_closedRetryDelay, timeProvider);
        await ReconnectIfNeededAsync();
    }

    private async Task RebuildAsync()
    {
        foreach (var attempt in Enumerable.Range(1, MaxRebuildAttempts))
        {
            await TearDownAsync();

            var becameLive = await TryBecomeLiveAsync();
            if (_disposed || becameLive)
            {
                return;
            }

            if (attempt < MaxRebuildAttempts)
            {
                await Task.Delay(_rebuildRetryDelay, timeProvider);
            }
        }

        // Still unreachable (e.g. offline). A failed attempt leaves a non-null, never-started
        // connection that won't auto-reconnect, so reset to a clean Disconnected state and
        // let the online/visibility listeners retry on the next resume rather than getting
        // stuck — and don't let the failure escape uncaught into OnPageVisible.
        await TearDownAsync();
        _connectionEventDispatcher.HandleClosed(null);
    }

    private async Task<bool> TryBecomeLiveAsync()
    {
        try
        {
            // Bound each attempt: right after an Android resume the radio may not be up
            // yet, and an unbounded StartAsync can hang on a dead handshake for tens of
            // seconds — the exact stall this rebuild exists to escape.
            using var cts = new CancellationTokenSource(_rebuildAttemptTimeout, timeProvider);
            await StartLiveConnectionAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task TearDownAsync()
    {
        if (_connection is null)
        {
            return;
        }

        // Detach all three first: the connection we're tearing down dispatches its callbacks
        // fire-and-forget off the receive loop, so leaving one attached lets a stale callback
        // later race the fresh connection — flip the UI to Disconnected or Reconnecting over a
        // live socket, fire a redundant reconnect, or advance the connection epoch (which
        // session recovery and catch-up are keyed on) for a transport that is already dead.
        _connection.Closed -= OnConnectionClosed;
        _connection.Reconnecting -= OnConnectionReconnecting;
        _connection.Reconnected -= OnConnectionReconnected;
        eventBinder.Unbind();
        await _connection.DisposeAsync();
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await TearDownAsync();
    }
}