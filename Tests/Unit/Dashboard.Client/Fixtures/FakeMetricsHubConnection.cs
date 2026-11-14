using Dashboard.Client.Contracts;

namespace Tests.Unit.Dashboard.Client.Fixtures;

// One handler registry keyed by wire method name, so a new server push costs a call to RaiseAsync
// and nothing else. It can also fail a scripted number of starts and raise the three lifecycle
// events, which is what makes the behaviour around an interruption expressible in a test at all.
public sealed class FakeMetricsHubConnection : IMetricsHubConnection
{
    private readonly Dictionary<string, List<Delegate>> _handlers = [];

    public int StartAttempts { get; private set; }

    public int FailedStartsRemaining { get; set; }

    // The proxy's session check answering the hub's negotiate as a signed-out request is: every start
    // fails as SignalR reports an HTTP refusal.
    public bool SignedOut { get; set; }

    public bool Disposed { get; private set; }

    // Holds a start open, so a test can dispose the module while a start that is going to succeed is
    // still in flight.
    public TaskCompletionSource? StartGate { get; set; }

    public event Func<Exception?, Task>? Closed;
    public event Func<Exception?, Task>? Reconnecting;
    public event Func<string?, Task>? Reconnected;

    public IDisposable On<T>(string methodName, Func<T, Task> handler)
    {
        var registered = _handlers.TryGetValue(methodName, out var existing)
            ? existing
            : _handlers[methodName] = [];

        registered.Add(handler);
        return new Registration(registered, handler);
    }

    public Task RaiseAsync<T>(string methodName, T payload) =>
        _handlers.TryGetValue(methodName, out var registered)
            ? Task.WhenAll(registered.OfType<Func<T, Task>>().ToList().Select(handler => handler(payload)))
            : Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartAttempts++;
        await Task.Yield();

        if (StartGate is { } gate)
        {
            await gate.Task;
        }

        if (SignedOut)
        {
            throw new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).",
                null, System.Net.HttpStatusCode.Unauthorized);
        }

        if (FailedStartsRemaining > 0)
        {
            FailedStartsRemaining--;
            throw new InvalidOperationException("hub unavailable");
        }
    }

    public Task RaiseClosedAsync(Exception? exception) =>
        Closed?.Invoke(exception) ?? Task.CompletedTask;

    public Task RaiseReconnectingAsync(Exception? exception) =>
        Reconnecting?.Invoke(exception) ?? Task.CompletedTask;

    public Task RaiseReconnectedAsync(string? connectionId = "connection-1") =>
        Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private sealed class Registration(List<Delegate> registered, Delegate handler) : IDisposable
    {
        public void Dispose() => registered.Remove(handler);
    }
}