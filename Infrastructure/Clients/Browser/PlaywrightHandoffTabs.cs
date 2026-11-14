using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

// The handoff tabs of one browser: pages opened in a person's context that no browse session
// admits, so nothing the agent calls can reach them. A handoff is its tab plus every popup it
// opened (a Google sign-in window); the page on top is the newest one still open, and that is the
// one the live view shows and types into.
internal sealed class PlaywrightHandoffTabs(Func<string, Task<IBrowserContext>> contextFor)
{
    // The longest side a frame is encoded at, and the most a small viewport is scaled up by: a phone
    // shows the page at two or three device pixels to each CSS pixel, so a frame at the CSS size alone
    // reads soft, and the cap keeps a desktop view at a fraction of a full-size frame's bytes.
    private const int MaxFrameSide = 1600;
    private const double MaxFrameScale = 2;

    // The sizes a view may ask the tab to be drawn at: a phone held either way up, up to a desktop's.
    private const int MinViewport = 320;
    private const int MaxViewport = 1920;

    // How often the page on top is asked where its text fields are and whether it is scrolled.
    private static readonly TimeSpan _fieldsInterval = TimeSpan.FromMilliseconds(250);

    // The text fields of the page as fractions of the viewport, with the keyboard each wants: where and
    // what kind, never a value — and whether the page is scrolled. Main frame only — a login form
    // inside a frame is typed into through the view's keyboard button instead.
    private const string FieldsScript =
        """
        () => {
          const w = window.innerWidth, h = window.innerHeight;
          const skip = new Set(['hidden', 'checkbox', 'radio', 'submit', 'button', 'reset', 'file', 'image', 'range', 'color']);
          const kindOf = e => {
            if (e.isContentEditable || e.tagName === 'TEXTAREA') return 'text';
            const type = (e.getAttribute('type') || 'text').toLowerCase();
            const auto = (e.getAttribute('autocomplete') || '').toLowerCase();
            const mode = (e.getAttribute('inputmode') || '').toLowerCase();
            if (type === 'password') return 'password';
            if (auto.includes('one-time-code')) return 'one-time-code';
            if (type === 'email' || mode === 'email' || auto.includes('email')) return 'email';
            if (type === 'tel' || mode === 'tel' || auto.includes('tel')) return 'tel';
            if (type === 'number' || mode === 'numeric' || mode === 'decimal') return 'number';
            if (type === 'url' || mode === 'url') return 'url';
            return 'text';
          };
          const fields = [];
          for (const e of document.querySelectorAll('input, textarea, [contenteditable=""], [contenteditable="true"]')) {
            if (e.tagName === 'INPUT' && skip.has((e.getAttribute('type') || 'text').toLowerCase())) continue;
            if (e.disabled || e.readOnly) continue;
            const r = e.getBoundingClientRect();
            if (r.width < 4 || r.height < 4 || r.bottom <= 0 || r.right <= 0 || r.top >= h || r.left >= w) continue;
            fields.push({ x: r.left / w, y: r.top / h, width: r.width / w, height: r.height / h, kind: kindOf(e), focused: e === document.activeElement });
            if (fields.length === 40) break;
          }
          return { scrolled: window.scrollX !== 0 || window.scrollY !== 0, fields };
        }
        """;

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

    public async Task<IAsyncDisposable> StreamAsync(
        string id, Func<HandoffFrame, Task> onFrame, Func<IReadOnlyList<HandoffField>, Task>? onFields, CancellationToken ct)
    {
        var handoff = _handoffs.GetValueOrDefault(id)
                      ?? throw new InvalidOperationException($"No handoff '{id}'.");
        return await handoff.StartViewAsync(() => new HandoffStream(handoff, onFrame, onFields));
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

        if (input is HandoffInput.Resize resize)
        {
            await _handoffs[id].ResizeAsync(new ViewportSize
            {
                Width = Math.Clamp(resize.Width, MinViewport, MaxViewport),
                Height = Math.Clamp(resize.Height, MinViewport, MaxViewport)
            });
            return;
        }

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

        private readonly SemaphoreSlim _views = new(1, 1);
        private HandoffStream? _view;

        public Handoff(IPage page) => Adopt(page);

        // Raised when the page on top may have changed — a popup arrived or a page closed — or the size
        // it is drawn at did.
        public event Action? TopChanged;

        // The size the view asked for; null until it asks, and the tab keeps the browser's own.
        public ViewportSize? Size { get; private set; }

        // One view at a time: a newer one ends the older before it starts casting, so the older's own
        // ending later — its socket noticed gone long after — stops nothing the newer one runs.
        public async Task<IAsyncDisposable> StartViewAsync(Func<HandoffStream> make)
        {
            await _views.WaitAsync();
            try
            {
                if (_view is { } older)
                {
                    await older.DisposeAsync();
                }

                _view = make();
                return await _view.StartAsync();
            }
            finally
            {
                _views.Release();
            }
        }

        public async Task ResizeAsync(ViewportSize size)
        {
            if (Size is { } now && now.Width == size.Width && now.Height == size.Height)
            {
                return;
            }

            Size = size;
            await Task.WhenAll(Pages.Where(p => !p.IsClosed).Select(SizeAsync));
            TopChanged?.Invoke();
        }

        private async Task SizeAsync(IPage page)
        {
            if (Size is not { } size)
            {
                return;
            }

            try
            {
                await page.SetViewportSizeAsync(size.Width, size.Height);
            }
            catch (PlaywrightException)
            {
                // Closed under the resize; the next page on top is sized as it is adopted.
            }
        }

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
            // Playwright holds the page until a dialog is answered, and the person never sees one
            // through the screencast: it is dismissed, so nothing is agreed to on their behalf.
            page.Dialog += async (_, dialog) => await dialog.DismissAsync();
            // Every main-frame navigation request, redirect hops included — a single sign-on's
            // intermediate pages set cookies of their own, and an unticked login must leave
            // those out of every save too.
            page.Request += (_, request) =>
            {
                if (request.IsNavigationRequest && request.Frame.ParentFrame is null)
                {
                    Record(request.Url);
                }
            };
            page.Popup += async (_, popup) =>
            {
                Adopt(popup);
                // A sign-in window is drawn at the view's size like the tab that opened it.
                await SizeAsync(popup);
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
    // popup opens or the top closes, and started again at a new size when the view asks for one; and
    // where that page's text fields are, asked every little while and told only when it changes.
    private sealed class HandoffStream(
        Handoff handoff, Func<HandoffFrame, Task> onFrame, Func<IReadOnlyList<HandoffField>, Task>? onFields) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private IPage? _casting;
        private ViewportSize? _castSize;
        private bool _disposed;
        private Task _fields = Task.CompletedTask;

        // Camoufox's screencast draws the page as if it were never scrolled, while clicks land where the
        // scrolled page is: a scrolled page's frames are screenshots instead, which are taken where it is.
        private volatile bool _scrolled;
        private int _shooting;

        public async Task<IAsyncDisposable> StartAsync()
        {
            handoff.TopChanged += OnTopChanged;
            await FollowTopAsync();
            _fields = WatchPageAsync(_stop.Token);
            return this;
        }

        // A frame as the person should see it: the screencast's own while the page is at its top, and a
        // screenshot of where it is once scrolled — the newest only, one at a time, the frame that asked
        // being only the sign that something changed.
        private async Task ShowAsync(IPage page, HandoffFrame frame)
        {
            if (!_scrolled)
            {
                await onFrame(frame);
                return;
            }

            await ShootAsync(page);
        }

        private async Task ShootAsync(IPage page)
        {
            if (Interlocked.Exchange(ref _shooting, 1) == 1)
            {
                return;
            }

            try
            {
                var viewport = page.ViewportSize;
                var jpeg = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 80 });
                if (!_disposed)
                {
                    await onFrame(new HandoffFrame(jpeg, viewport?.Width ?? 0, viewport?.Height ?? 0));
                }
            }
            catch (PlaywrightException)
            {
                // Closed or navigating under the shot; the next change asks again.
            }
            finally
            {
                Volatile.Write(ref _shooting, 0);
            }
        }

        private void OnTopChanged() => _ = FollowTopAsync();

        private async Task FollowTopAsync()
        {
            await _gate.WaitAsync();
            try
            {
                var top = handoff.Top;
                var size = handoff.Size;
                if (_disposed || (ReferenceEquals(top, _casting) && Equals(size, _castSize)))
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
                    Size = FrameSize(top.ViewportSize),
                    OnFrame = frame => ShowAsync(top, new HandoffFrame(frame.Data, frame.ViewportWidth, frame.ViewportHeight))
                });
                _casting = top;
                _castSize = size;
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

        // The bounds a frame is fitted into, keeping the page's own shape.
        private static ScreencastSize FrameSize(PageViewportSizeResult? viewport)
        {
            var width = viewport?.Width ?? 1920;
            var height = viewport?.Height ?? 1080;
            var scale = Math.Min(MaxFrameScale, (double)MaxFrameSide / Math.Max(width, height));
            return new ScreencastSize { Width = (int)(width * scale), Height = (int)(height * scale) };
        }

        private async Task WatchPageAsync(CancellationToken ct)
        {
            IReadOnlyList<HandoffField>? told = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (handoff.Top is { } top)
                    {
                        JsonElement? answer = null;
                        try
                        {
                            answer = await top.EvaluateAsync<JsonElement>(FieldsScript);
                        }
                        catch (PlaywrightException)
                        {
                            // Mid-navigation, or closed: asked again on the next round.
                        }

                        if (answer is { ValueKind: JsonValueKind.Object } page)
                        {
                            var scrolled = page.TryGetProperty("scrolled", out var s) && s.ValueKind == JsonValueKind.True;
                            if (scrolled != _scrolled)
                            {
                                _scrolled = scrolled;
                                if (scrolled)
                                {
                                    await ShootAsync(top);
                                }
                            }

                            var fields = FieldsOf(page.TryGetProperty("fields", out var f) ? f : default);
                            if (onFields is not null && (told is null || !told.SequenceEqual(fields)))
                            {
                                told = fields;
                                await onFields(fields);
                            }
                        }
                    }

                    await Task.Delay(_fieldsInterval, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // The view ended.
            }
        }

        private static IReadOnlyList<HandoffField> FieldsOf(JsonElement answer) =>
            answer.ValueKind != JsonValueKind.Array
                ? []
                : answer.EnumerateArray().Select(f => new HandoffField(
                    Number(f, "x"), Number(f, "y"), Number(f, "width"), Number(f, "height"),
                    f.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString()! : "text",
                    f.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True)).ToList();

        private static double Number(JsonElement field, string name) =>
            field.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

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
            _castSize = null;
        }

        public async ValueTask DisposeAsync()
        {
            handoff.TopChanged -= OnTopChanged;
            await _gate.WaitAsync();
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await _stop.CancelAsync();
                await StopCastingAsync();
            }
            finally
            {
                _gate.Release();
            }

            await _fields;
        }
    }
}