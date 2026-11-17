namespace Domain.Agents;

public sealed class ChatThreadContext : IDisposable
{
    private Action? _onComplete;
    private int _isDisposed;

    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationTokenSource Cts { get; } = new();

    // The group of this conversation before this one, cancelled (a stop, a clear, a failed setup) and maybe
    // still tearing down: keeping its stopped turn among it. This one's first turn waits for it, so the
    // conversation it reads and adds to is the one the person saw, the stopped turn before what followed.
    public Task Predecessor { get; init; } = Task.CompletedTask;

    // Done once the group on this context wrote everything it ever will.
    public Task Ended => _ended.Task;

    public void End() => _ended.TrySetResult();

    public void RegisterCompletionCallback(Action onComplete)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        _onComplete = onComplete;
    }

    public CancellationTokenSource GetLinkedTokenSource(CancellationToken externalToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        return CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        _onComplete?.Invoke();
        _onComplete = null;

        Cts.Cancel();
        Cts.Dispose();
    }
}