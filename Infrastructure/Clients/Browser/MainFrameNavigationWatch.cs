using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

// Whether the page has sent itself somewhere it has not arrived yet. A bot check's script submits
// a form or replaces the location once it has run, and until the server answers, the page still
// shows the check: reading it then extracts "One moment." as the content, and a script evaluated
// as the new document commits throws instead. Started before the browse's own navigation, because
// an inline script can send the page on before the goto returns; that navigation's own commit
// clears it like any other.
internal sealed class MainFrameNavigationWatch : IDisposable
{
    private readonly IPage _page;
    private readonly Lock _lock = new();
    private IRequest? _pending;
    private TaskCompletionSource _arrived = NewSignal();

    public MainFrameNavigationWatch(IPage page)
    {
        _page = page;
        _page.Request += OnRequest;
        _page.RequestFailed += OnRequestFailed;
        _page.FrameNavigated += OnFrameNavigated;
    }

    public bool InFlight
    {
        get
        {
            lock (_lock)
            {
                return _pending is not null;
            }
        }
    }

    // Answers once the navigation in flight commits or fails, or at the deadline: a follow-up the
    // server never answers is read as what is on screen rather than holding the browse to its cap.
    public async Task WaitAsync(TimeSpan deadline, CancellationToken ct)
    {
        Task arrived;
        lock (_lock)
        {
            if (_pending is null)
            {
                return;
            }

            arrived = _arrived.Task;
        }

        await Task.WhenAny(arrived, Task.Delay(deadline, ct));
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        _page.Request -= OnRequest;
        _page.RequestFailed -= OnRequestFailed;
        _page.FrameNavigated -= OnFrameNavigated;
    }

    private void OnRequest(object? sender, IRequest request)
    {
        if (!request.IsNavigationRequest || request.Frame.ParentFrame is not null)
        {
            return;
        }

        lock (_lock)
        {
            if (_arrived.Task.IsCompleted)
            {
                _arrived = NewSignal();
            }

            _pending = request;
        }
    }

    private void OnRequestFailed(object? sender, IRequest request)
    {
        lock (_lock)
        {
            if (ReferenceEquals(request, _pending))
            {
                Arrive();
            }
        }
    }

    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        if (frame.ParentFrame is not null)
        {
            return;
        }

        lock (_lock)
        {
            Arrive();
        }
    }

    private void Arrive()
    {
        _pending = null;
        _arrived.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}