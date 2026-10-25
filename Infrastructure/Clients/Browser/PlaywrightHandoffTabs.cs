using System.Collections.Concurrent;
using Domain.Contracts;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

// The handoff tabs of one browser: pages opened in a person's context that no browse session
// admits, so nothing the agent calls can reach them. A handoff is its tab plus every popup it
// opened (a Google sign-in window); the page on top is the newest one still open, and that is the
// one the live view shows and types into.
internal sealed class PlaywrightHandoffTabs(Func<string, Task<IBrowserContext>> contextFor)
{
    // The scale frames are encoded at: enough to read a login form on a phone, a fraction of the
    // bytes of the full 1920x1080 viewport.
    private static readonly ScreencastSize _frameSize = new() { Width = 1280, Height = 720 };

    private readonly ConcurrentDictionary<string, Handoff> _handoffs = new();

    public async Task<string> OpenAsync(string person, string url, CancellationToken ct)
    {
        var context = await contextFor(person);
        var page = await context.NewPageAsync();
        var handoff = new Handoff(page);
        var id = Guid.NewGuid().ToString("N");
        _handoffs[id] = handoff;
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // A slow or erroring login page is still the person's to look at; the view shows
            // whatever arrived.
        }

        return id;
    }

    public bool IsOpen(string id) => _handoffs.TryGetValue(id, out var handoff) && handoff.Top is not null;

    public async Task CloseAsync(string id)
    {
        if (!_handoffs.TryRemove(id, out var handoff))
        {
            return;
        }

        foreach (var page in handoff.Pages)
        {
            try
            {
                await page.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // Already gone with its connection.
            }
        }
    }

    public Task<IAsyncDisposable> StreamAsync(string id, Func<HandoffFrame, Task> onFrame, CancellationToken ct)
    {
        var handoff = _handoffs.GetValueOrDefault(id)
                      ?? throw new InvalidOperationException($"No handoff '{id}'.");
        var stream = new HandoffStream(handoff, onFrame);
        return stream.StartAsync();
    }

    public async Task SendInputAsync(string id, HandoffInput input, CancellationToken ct)
    {
        if (_handoffs.GetValueOrDefault(id)?.Top is not { } page)
        {
            return;
        }

        var viewport = page.ViewportSize ?? new PageViewportSizeResult { Width = 1920, Height = 1080 };
        float X(double fraction) => (float)(Math.Clamp(fraction, 0, 1) * viewport.Width);
        float Y(double fraction) => (float)(Math.Clamp(fraction, 0, 1) * viewport.Height);

        try
        {
            switch (input)
            {
                case HandoffInput.MouseMove move:
                    await page.Mouse.MoveAsync(X(move.X), Y(move.Y));
                    break;
                case HandoffInput.MouseDown down:
                    await page.Mouse.MoveAsync(X(down.X), Y(down.Y));
                    await page.Mouse.DownAsync(new MouseDownOptions { Button = ButtonOf(down.Button) });
                    break;
                case HandoffInput.MouseUp up:
                    await page.Mouse.MoveAsync(X(up.X), Y(up.Y));
                    await page.Mouse.UpAsync(new MouseUpOptions { Button = ButtonOf(up.Button) });
                    break;
                case HandoffInput.Wheel wheel:
                    await page.Mouse.WheelAsync((float)wheel.DeltaX, (float)wheel.DeltaY);
                    break;
                case HandoffInput.KeyDown key:
                    await page.Keyboard.DownAsync(key.Key);
                    break;
                case HandoffInput.KeyUp key:
                    await page.Keyboard.UpAsync(key.Key);
                    break;
                case HandoffInput.Text text when text.Value.Length > 0:
                    await page.Keyboard.InsertTextAsync(text.Value);
                    break;
            }
        }
        catch (PlaywrightException)
        {
            // A key name the browser does not know, or a page that closed under the event: one
            // lost keystroke, never a dead view.
        }
    }

    public IReadOnlyList<string> VisitedHosts(string id) =>
        _handoffs.GetValueOrDefault(id)?.VisitedHosts ?? [];

    private static MouseButton ButtonOf(string button) => button switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left
    };

    private sealed class Handoff
    {
        private readonly List<IPage> _pages = [];
        private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);

        public Handoff(IPage page) => Adopt(page);

        // Raised when the page on top may have changed: a popup arrived or a page closed.
        public event Action? TopChanged;

        public IReadOnlyList<IPage> Pages
        {
            get
            {
                lock (_pages)
                {
                    return [.. _pages];
                }
            }
        }

        public IPage? Top
        {
            get
            {
                lock (_pages)
                {
                    return _pages.LastOrDefault(p => !p.IsClosed);
                }
            }
        }

        public IReadOnlyList<string> VisitedHosts
        {
            get
            {
                lock (_hosts)
                {
                    return [.. _hosts];
                }
            }
        }

        private void Adopt(IPage page)
        {
            lock (_pages)
            {
                _pages.Add(page);
            }

            Record(page.Url);
            // Playwright holds the page until a dialog is answered; a login's "are you sure" must
            // not freeze the person's view.
            page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
            page.FrameNavigated += (_, frame) =>
            {
                if (frame.ParentFrame is null)
                {
                    Record(frame.Url);
                }
            };
            page.Popup += (_, popup) =>
            {
                Adopt(popup);
                TopChanged?.Invoke();
            };
            page.Close += (_, _) => TopChanged?.Invoke();
        }

        private void Record(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                lock (_hosts)
                {
                    _hosts.Add(uri.Host);
                }
            }
        }
    }

    // One live view's frames: a screencast on the page on top, moved to the new top whenever a
    // popup opens or the top closes.
    private sealed class HandoffStream(Handoff handoff, Func<HandoffFrame, Task> onFrame) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IPage? _casting;
        private bool _disposed;

        public async Task<IAsyncDisposable> StartAsync()
        {
            handoff.TopChanged += OnTopChanged;
            await FollowTopAsync();
            return this;
        }

        private void OnTopChanged() => _ = FollowTopAsync();

        private async Task FollowTopAsync()
        {
            await _gate.WaitAsync();
            try
            {
                var top = handoff.Top;
                if (_disposed || ReferenceEquals(top, _casting))
                {
                    return;
                }

                await StopCastingAsync();
                if (top is null)
                {
                    return;
                }

                await top.Screencast.StartAsync(new ScreencastStartOptions
                {
                    Size = _frameSize,
                    OnFrame = frame => onFrame(new HandoffFrame(frame.Data, frame.ViewportWidth, frame.ViewportHeight))
                });
                _casting = top;
            }
            catch (PlaywrightException)
            {
                // The page closed as the cast started; the next change moves on.
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task StopCastingAsync()
        {
            if (_casting is { IsClosed: false } casting)
            {
                try
                {
                    await casting.Screencast.StopAsync();
                }
                catch (PlaywrightException)
                {
                    // Closed underneath the stop.
                }
            }

            _casting = null;
        }

        public async ValueTask DisposeAsync()
        {
            handoff.TopChanged -= OnTopChanged;
            await _gate.WaitAsync();
            try
            {
                _disposed = true;
                await StopCastingAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}