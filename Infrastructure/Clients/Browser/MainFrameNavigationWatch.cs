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
    private bool _expecting;
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
                return _pending is not null || _expecting;
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
            if (_pending is null && !_expecting)
            {
                return;
            }

            arrived = _arrived.Task;
        }

        await Task.WhenAny(arrived, Task.Delay(deadline, ct));
        ct.ThrowIfCancellationRequested();
    }

    // The page says it is leaving (BrowserScripts.LeavingMark) before any request has been seen:
    // Firefox stops parsing a page whose script navigates and asks for the next document only later,
    // and in between the page is complete with nothing in flight. Until the next main-frame commit,
    // or a failure of the request it makes, the flight is on.
    public void ExpectNextCommit()
    {
        lock (_lock)
        {
            if (_arrived.Task.IsCompleted)
            {
                _arrived = NewSignal();
            }

            _expecting = true;
        }
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

    // Only the requested document's commit ends the flight. Firefox reports a request from its parent
    // process and a commit from the page's own, so under load the commit of the document already on
    // screen can arrive after the request its script made next; taken as the arrival, it cleared a
    // follow-up still in flight and the browse read the bot check itself. A redirect is a request of
    // its own for each hop, so the pending one is always the address the page commits.
    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        if (frame.ParentFrame is not null)
        {
            return;
        }

        lock (_lock)
        {
            if (_pending is null ? _expecting : SameDocument(_pending.Url, frame.Url))
            {
                Arrive();
            }
        }
    }

    private static bool SameDocument(string requested, string committed) =>
        string.Equals(WithoutFragment(requested), WithoutFragment(committed), StringComparison.Ordinal);

    private static string WithoutFragment(string url) => url.Split('#', 2)[0];

    private void Arrive()
    {
        _pending = null;
        _expecting = false;
        _arrived.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}