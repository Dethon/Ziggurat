using System.Diagnostics;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class PlaywrightWebBrowser(
    ICaptchaSolver? captchaSolver = null,
    string? wsEndpoint = null,
    Func<Task<IBrowser>>? browserFactory = null)
    : IWebBrowser, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly BrowserSessionManager _sessions = new(
        idleTimeout: TimeSpan.FromMinutes(30),
        pruneInterval: TimeSpan.FromMinutes(5));
    private readonly ModalDismisser _modalDismisser = new();
    private readonly AccessibilitySnapshotService _snapshotService = new();
    private readonly Random _random = new();
    private bool _initialized;
    // Bumped under _initLock each time a fresh browser connection is established. Lets the reconnect
    // path replace only the connection that actually failed, so concurrent callers don't tear down a
    // connection another caller already re-established.
    private long _connectionGeneration;
    private const int MaxCaptchaRetries = 2;
    private const int DefaultOperationTimeoutMs = 15_000;

    // Resolving a ref is an existence check, not a wait for something in flight: the element was in
    // the snapshot the agent is holding, so either it is still there or the page moved on. Kept
    // generous enough to ride out an in-progress re-render, but far below the operation timeout.
    private const int RefResolutionTimeoutMs = 2_000;
    private const int ConnectionRetryAttempts = 3;
    private const int ConnectionRetryDelayMs = 2_000;

    public async Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default)
    {
        if (!ValidateUrl(request.Url))
        {
            return CreateErrorResult(request.SessionId, request.Url,
                "Invalid URL. Only http and https URLs are supported.");
        }

        try
        {
            // Why: all calls for one session share a single IPage, so concurrent navigations
            // would interrupt each other ("Navigation to X is interrupted by another navigation
            // to Y"). Serialize per-session work so parallel tool calls queue instead of clobber.
            using var sessionLock = await _sessions.AcquireSessionLockAsync(request.SessionId, ct);

            // Why: the Camoufox WebSocket can drop mid-navigation. ExecuteWithReconnectAsync
            // reconnects and re-runs the navigation on a fresh page instead of leaking
            // "Target page, context or browser has been closed" to the caller.
            return await ExecuteWithReconnectAsync(() => NavigateOnceAsync(request, ct));
        }
        catch (PlaywrightException ex) when (IsConnectionClosed(ex))
        {
            // The connection was still dead after a reconnect+retry (e.g. a page that crashes the
            // browser process on every load). Surface a clean, actionable message rather than the
            // raw "Target page, context or browser has been closed", which is meaningless to the agent.
            return CreateErrorResult(request.SessionId, request.Url,
                "The browser connection dropped while loading the page and could not recover. " +
                "This usually means the page itself crashed the browser; try a different URL or try again later.");
        }
        catch (PlaywrightException ex)
        {
            return CreateErrorResult(request.SessionId, request.Url, DescribeNavigationError(ex.Message));
        }
        catch (TimeoutException)
        {
            return CreateErrorResult(request.SessionId, request.Url, "Request timed out");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Camoufox"))
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateErrorResult(request.SessionId, request.Url, $"Error: {ex.Message}");
        }
    }

    private async Task<BrowseResult> NavigateOnceAsync(BrowseRequest request, CancellationToken ct)
    {
        // The pre-navigation jitter and acquiring the page are independent, so the delay runs
        // alongside page creation (measured ~150ms) instead of after it. A random gap still precedes
        // GotoAsync — it just no longer costs anything on top of work already happening.
        var jitter = Task.Delay(_random.Next(50, 150), ct);
        var session = await _sessions.GetOrCreateAsync(request.SessionId, _context!, ct);
        var page = session.Page;

        await jitter;

        var navigationTimedOut = false;

        try
        {
            await page.GotoAsync(request.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            // Navigation timed out — check if we have content loaded and proceed with it
            var currentUrl = page.Url;
            if (!string.IsNullOrEmpty(currentUrl) && currentUrl != "about:blank")
            {
                navigationTimedOut = true;
            }
            else
            {
                throw;
            }
        }
        catch (TimeoutException)
        {
            var currentUrl = page.Url;
            if (!string.IsNullOrEmpty(currentUrl) && currentUrl != "about:blank")
            {
                navigationTimedOut = true;
            }
            else
            {
                throw;
            }
        }

        _sessions.UpdateCurrentUrl(request.SessionId, page.Url);

        var html = await page.ContentAsync();
        var captchaRetries = 0;
        while (ContainsCaptcha(html) && captchaRetries < MaxCaptchaRetries)
        {
            var captchaResult = await TrySolveCaptchaAsync(page, request.Url, html, ct);
            if (!captchaResult.Solved)
            {
                return new BrowseResult(
                    SessionId: request.SessionId,
                    Url: page.Url,
                    Status: BrowseStatus.CaptchaRequired,
                    Title: null,
                    Content: captchaResult.Message,
                    ContentLength: 0,
                    Truncated: false,
                    Metadata: null,
                    StructuredData: null,
                    DismissedModals: null,
                    ErrorMessage: captchaResult.Message
                );
            }

            // Refresh page after setting cookie
            await page.ReloadAsync(new PageReloadOptions
            { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
            html = await page.ContentAsync();
            captchaRetries++;
        }

        var dismissedModals = await _modalDismisser.DismissModalsAsync(page, ct);

        // Extract structured data before stripping DOM noise,
        // because StripDomNoiseAsync removes <script> tags including ld+json
        var structuredData = ExtractStructuredData(html);

        // Strip hidden overlays, dismissed modals, and non-content noise from DOM
        // to prevent them from consuming the content budget during HTML processing
        await StripDomNoiseAsync(page);

        if (request.ScrollToLoad)
        {
            await ScrollToLoadAsync(page, request.ScrollSteps, ct);
        }

        await WaitForDomStabilityAsync(page, ct: ct);

        html = await page.ContentAsync();
        var processed = await HtmlProcessor.ProcessAsync(request, html, ct);

        var status = navigationTimedOut || processed.IsPartial
            ? BrowseStatus.Partial
            : BrowseStatus.Success;

        var errorMessage = processed.ErrorMessage;
        if (navigationTimedOut)
        {
            var timeoutMsg = "Page did not fully load (DOMContentLoaded timeout). Content may be incomplete.";
            errorMessage = string.IsNullOrEmpty(errorMessage) ? timeoutMsg : $"{timeoutMsg} {errorMessage}";
        }

        return new BrowseResult(
            SessionId: request.SessionId,
            Url: page.Url,
            Status: status,
            Title: processed.Title,
            Content: processed.Content,
            ContentLength: processed.ContentLength,
            Truncated: processed.Truncated,
            Metadata: processed.Metadata,
            StructuredData: structuredData.Count > 0 ? structuredData : null,
            DismissedModals: dismissedModals,
            ErrorMessage: errorMessage
        )
        {
            ImageCount = processed.ImageCount,
            ImagesBeyondWindow = processed.ImagesBeyondWindow
        };
    }

    public async Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
    {
        var session = _sessions.Get(sessionId);
        if (session == null)
        {
            return new BrowseResult(
                sessionId,
                "",
                BrowseStatus.SessionNotFound,
                null,
                null,
                0,
                false,
                null,
                null,
                null,
                "No active browser session found"
            );
        }

        try
        {
            var html = await session.Page.ContentAsync();
            var request = new BrowseRequest(SessionId: sessionId, Url: session.CurrentUrl);
            var processed = await HtmlProcessor.ProcessAsync(request, html, ct);

            return new BrowseResult(
                SessionId: sessionId,
                Url: session.Page.Url,
                Status: processed.IsPartial ? BrowseStatus.Partial : BrowseStatus.Success,
                Title: processed.Title,
                Content: processed.Content,
                ContentLength: processed.ContentLength,
                Truncated: processed.Truncated,
                Metadata: processed.Metadata,
                StructuredData: null,
                DismissedModals: null,
                ErrorMessage: processed.ErrorMessage
            )
            {
                ImageCount = processed.ImageCount,
                ImagesBeyondWindow = processed.ImagesBeyondWindow
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResult(sessionId, session.CurrentUrl, $"Error: {ex.Message}");
        }
    }

    public async Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null || session.Page.IsClosed)
        {
            _sessions.Remove(request.SessionId);
            return new SnapshotResult(request.SessionId, null, null, 0, "Session not found. Use web_browse first.");
        }

        try
        {
            var result = await _snapshotService.CaptureAsync(session.Page, request.Selector, request.SessionId);
            return new SnapshotResult(request.SessionId, session.Page.Url, result.Snapshot, result.RefCount, null);
        }
        catch (Exception ex) when (IsConnectionClosed(ex))
        {
            // The connection dropped — the page's content is gone, so reconnecting would only
            // give a blank page. Drop the dead session and tell the caller to web_browse again
            // instead of leaking the raw "has been closed" Playwright message.
            _sessions.Remove(request.SessionId);
            return new SnapshotResult(request.SessionId, null, null, 0, "Session not found. Use web_browse first.");
        }
        catch (Exception ex)
        {
            return new SnapshotResult(request.SessionId, session.Page.Url, null, 0, ex.Message);
        }
    }

    public async Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null || session.Page.IsClosed)
        {
            _sessions.Remove(request.SessionId);
            return new WebActionResult(request.SessionId, WebActionStatus.SessionNotFound,
                null, false, null, null, "Session not found. Use web_browse first.");
        }

        var page = session.Page;
        var urlBefore = page.Url;

        try
        {
            // Why: an action can trigger navigation; serialize it against other same-session
            // calls so it cannot interrupt (or be interrupted by) a concurrent navigation.
            using var sessionLock = await _sessions.AcquireSessionLockAsync(request.SessionId, ct);

            return request.Action switch
            {
                WebActionType.Back => await ExecuteBackAsync(request, page, urlBefore, ct),
                _ => await ExecuteElementActionAsync(request, page, urlBefore, ct)
            };
        }
        catch (PlaywrightException ex) when (IsConnectionClosed(ex))
        {
            // The connection dropped — the page's state is gone. Drop the dead session and
            // tell the caller to web_browse again rather than leaking the raw Playwright error.
            _sessions.Remove(request.SessionId);
            return new WebActionResult(request.SessionId, WebActionStatus.SessionNotFound,
                null, false, null, null, "Session lost (browser disconnected). Call web_browse to start again.");
        }
        catch (TimeoutException)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Timeout,
                page.Url, false, null, null, "Operation timed out.");
        }
        catch (PlaywrightException ex)
        {
            var status = ex.Message.Contains("not found") || ex.Message.Contains("no element")
                ? WebActionStatus.ElementNotFound
                : WebActionStatus.Error;
            return new WebActionResult(request.SessionId, status,
                page.Url, false, null, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                page.Url, false, null, null, ex.Message);
        }
    }

    private async Task<WebActionResult> ExecuteElementActionAsync(
        WebActionRequest request, IPage page, string urlBefore, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Ref))
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                page.Url, false, null, null, $"ref is required for {request.Action} action.");
        }

        var locator = AccessibilitySnapshotService.ResolveRef(page, request.Ref);
        try
        {
            await locator.WaitForAsync(
                new() { State = WaitForSelectorState.Visible, Timeout = RefResolutionTimeoutMs });
        }
        catch (TimeoutException)
        {
            // A ref that is not on the page is stale, not slow — the page re-rendered, or a browse
            // replaced the document, and no amount of extra waiting brings it back. Waiting the full
            // operation timeout here burned 15s only to report "Operation timed out.", which told
            // the agent nothing it could act on. Fail fast and name the recovery instead.
            return new WebActionResult(request.SessionId, WebActionStatus.ElementNotFound,
                page.Url, false, null, null,
                $"Element {request.Ref} is no longer on the page — the page has changed since the " +
                "snapshot was taken. Call web_snapshot to get current refs, then retry.");
        }

        // Capture before-snapshot for diffing — use preserveRefs to avoid clearing the
        // data-ref attributes that the locator depends on
        var before = await _snapshotService.CaptureAsync(page, null, request.SessionId, preserveRefs: true);
        var beforeLines = SnapshotLinesForDiff(before.Snapshot);

        switch (request.Action)
        {
            case WebActionType.Click:
                await locator.ClickAsync(new() { Force = request.Force });
                break;
            case WebActionType.Type:
                await locator.ClearAsync();
                await locator.PressSequentiallyAsync(request.Value ?? "", new() { Delay = 50 });
                break;
            case WebActionType.Fill:
                await locator.FillAsync(request.Value ?? "");
                break;
            case WebActionType.Select:
                await locator.SelectOptionAsync(request.Value ?? "");
                break;
            case WebActionType.Press:
                await locator.PressAsync(request.Value ?? "Enter");
                break;
            case WebActionType.Clear:
                await locator.ClearAsync();
                break;
            case WebActionType.Hover:
                await locator.HoverAsync();
                break;
            case WebActionType.Focus:
                await locator.FocusAsync();
                break;
            case WebActionType.Drag:
                if (string.IsNullOrEmpty(request.EndRef))
                {
                    return new WebActionResult(request.SessionId, WebActionStatus.Error,
                        page.Url, false, null, null, "endRef is required for drag action.");
                }

                await locator.DragToAsync(AccessibilitySnapshotService.ResolveRef(page, request.EndRef));
                break;
            default:
                return new WebActionResult(request.SessionId, WebActionStatus.Error,
                    page.Url, false, null, null, $"Unhandled element action: {request.Action}");
        }

        if (request.WaitForNavigation)
        {
            try
            {
                await page.WaitForURLAsync(url => url != urlBefore,
                    new() { Timeout = 30000 });
            }
            catch (TimeoutException)
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                    new() { Timeout = 15000 });
            }
        }
        else
        {
            await SmartWaitAsync(page, request, ct);
        }

        var navigationOccurred = page.Url != urlBefore;
        _sessions.UpdateCurrentUrl(request.SessionId, page.Url);

        var after = await _snapshotService.CaptureAsync(page, null, request.SessionId);

        // On navigation the entire page changed — return the new snapshot directly
        // instead of a diff that would duplicate all content as removed + added lines
        var snapshot = navigationOccurred
            ? after.Snapshot
            : BuildSnapshotDiff(beforeLines, after.Snapshot, request.Ref!, request.Action);

        return new WebActionResult(request.SessionId, WebActionStatus.Success,
            page.Url, navigationOccurred, snapshot, null, null);
    }

    private static Dictionary<string, int> SnapshotLinesForDiff(string snapshot)
    {
        var counts = new Dictionary<string, int>();
        foreach (var line in snapshot.Split('\n').Select(StripRefs))
        {
            counts[line] = counts.GetValueOrDefault(line) + 1;
        }
        return counts;
    }

    private static string StripRefs(string line)
        => System.Text.RegularExpressions.Regex.Replace(line, @"\s*\[ref=e\d+\]", "");

    private static string BuildSnapshotDiff(
        Dictionary<string, int> beforeCounts, string afterSnapshot, string actedRef, WebActionType action)
    {
        var afterLines = afterSnapshot.Split('\n');
        var afterCounts = new Dictionary<string, int>();
        foreach (var line in afterLines.Select(StripRefs))
        {
            afterCounts[line] = afterCounts.GetValueOrDefault(line) + 1;
        }

        var refTag = $"[ref={actedRef}]";
        var actedLine = afterLines.FirstOrDefault(l => l.Contains(refTag));

        var removed = beforeCounts
            .SelectMany(kv =>
                Enumerable.Repeat(kv.Key, Math.Max(0, kv.Value - afterCounts.GetValueOrDefault(kv.Key))))
            .ToList();

        // Track how many surplus occurrences of each line to include as added
        var surplusBudget = afterCounts.ToDictionary(
            kv => kv.Key,
            kv => Math.Max(0, kv.Value - beforeCounts.GetValueOrDefault(kv.Key)));
        var added = new List<string>();
        foreach (var line in afterLines)
        {
            var stripped = StripRefs(line);
            if (surplusBudget.GetValueOrDefault(stripped) > 0)
            {
                added.Add(line);
                surplusBudget[stripped]--;
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{action} on {actedRef}]");
        if (actedLine is not null)
        {
            sb.AppendLine($"[after: {actedLine.TrimStart()}]");
        }

        if (removed.Count == 0 && added.Count == 0)
        {
            sb.AppendLine("[no visible changes]");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine();
        foreach (var line in removed)
        {
            sb.Append("- ");
            sb.AppendLine(line);
        }
        foreach (var line in added)
        {
            sb.Append("+ ");
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private async Task SmartWaitAsync(IPage page, WebActionRequest request, CancellationToken ct)
    {
        var (maxWaitMs, intervalMs) = request.Action switch
        {
            WebActionType.Type => (2000, 200),
            WebActionType.Click => (2000, 200),
            WebActionType.Focus => (2000, 200),
            WebActionType.Hover => (1000, 200),
            _ => (300, 300)
        };

        if (request.Ref is null || maxWaitMs <= intervalMs)
        {
            await Task.Delay(maxWaitMs, ct);
            return;
        }

        var targetSelector = $"[data-ref='{request.Ref}']";
        var previousHtml = await GetNearbyHtmlAsync(page, targetSelector);
        var elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            await Task.Delay(intervalMs, ct);
            elapsed += intervalMs;
            var currentHtml = await GetNearbyHtmlAsync(page, targetSelector);
            if (currentHtml == previousHtml)
            {
                break;
            }

            previousHtml = currentHtml;
        }
    }

    private static async Task<string> GetNearbyHtmlAsync(IPage page, string targetSelector)
    {
        return await page.EvaluateAsync<string>("""
            (selector) => {
                const el = document.querySelector(selector);
                if (!el) return '';
                const tags = ['FORM','SECTION','ARTICLE','MAIN','DIALOG','FIELDSET'];
                const roles = ['dialog','form','region','listbox','menu'];
                let container = el.parentElement;
                for (let i = 0; i < 4 && container; i++) {
                    if (tags.includes(container.tagName) ||
                        roles.includes(container.getAttribute('role'))) break;
                    container = container.parentElement;
                }
                return (container || el.parentElement || el).innerHTML.substring(0, 3000);
            }
        """, targetSelector);
    }

    private async Task<WebActionResult> ExecuteBackAsync(
        WebActionRequest request, IPage page, string urlBefore, CancellationToken ct)
    {
        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        _sessions.UpdateCurrentUrl(request.SessionId, page.Url);
        var snapshot = await _snapshotService.CaptureAsync(page, null, request.SessionId);

        return new WebActionResult(request.SessionId, WebActionStatus.Success,
            page.Url, page.Url != urlBefore, snapshot.Snapshot, null, null);
    }


    public async Task EvaluateOnSessionAsync(string sessionId, string script)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found");
        await session.Page.EvaluateAsync(script);
    }

    // The reading half of the escape hatch: what the page answers, for a test that has to ask the
    // DOM what a step left behind rather than infer it from the markdown. Internal for the same
    // reason its void sibling is public only by history -- nothing outside needs it.
    internal async Task<T> EvaluateOnSessionAsync<T>(string sessionId, string script)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found");
        return await session.Page.EvaluateAsync<T>(script);
    }

    // The fetch goes through the page that listed the picture. Camoufox holds the cookies, the
    // referer and the fingerprint, and a great many images are served only to a request carrying
    // them -- a bare HttpClient would answer 403 or a placeholder pixel on exactly the pages where
    // this stack is earning its keep. It is also what makes the ref meaningful: it resolves
    // against the live DOM, so no URL has to enter the model's context to be usable.
    public async Task<ImageFetchResult> FetchImagesAsync(
        ImageFetchRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session is null)
        {
            return new ImageFetchResult(request.SessionId, [], SessionMissing: true);
        }

        // Sequential rather than a LINQ projection over tasks: these are real fetches out of one
        // page, and firing eight at once through a single browser context buys nothing while
        // making a rate-limiting site read the burst as exactly what it is.
        var images = new List<FetchedImage>(request.Refs.Count);
        foreach (var imageRef in request.Refs)
        {
            ct.ThrowIfCancellationRequested();
            images.Add(await FetchOneAsync(session.Page, imageRef));
        }

        return new ImageFetchResult(request.SessionId, images);
    }

    private async Task<FetchedImage> FetchOneAsync(IPage page, string imageRef)
    {
        try
        {
            // Fetched from inside the page so the request is the page's own. The bytes come back
            // as base64 because the evaluate boundary carries text, not binary.
            var payload = await page.EvaluateAsync<string?>(
                $$"""
                  async (ref) => {
                      const img = document.querySelector(`[{{PageImageEntry.RefAttribute}}="${ref}"]`);
                      if (!img) return null;
                      const src = img.getAttribute('src');
                      if (!src) return null;

                      // The same ladder the entry's label came from, filename rung included, so
                      // what the model asked for and what a later note calls it are the same
                      // words. Stopping a rung short here would leave a picture the page named
                      // logo.png coming back nameless, and its note would say "i-3".
                      const fileName = src.startsWith('data:')
                          ? ''
                          : (src.split(/[?#]/)[0].split('/').pop() || '');
                      const label = (img.getAttribute('alt')
                          || img.closest('figure')?.querySelector('figcaption')?.textContent
                          || img.getAttribute('title')
                          || img.closest('a')?.textContent
                          || (/^[^\s/]+\.[A-Za-z0-9]{1,5}$/.test(fileName) ? fileName : '')
                          || '').trim().replace(/\s+/g, ' ').slice(0, 120);

                      const url = new URL(src, location.href).toString();

                      // Same-origin first: fetch keeps the bytes exactly as served, so no
                      // re-encoding and no loss.
                      if (new URL(url).origin === location.origin) {
                          try {
                              const response = await fetch(url, { credentials: 'include' });
                              if (response.ok) {
                                  const bytes = new Uint8Array(await response.arrayBuffer());
                                  let binary = '';
                                  for (let i = 0; i < bytes.length; i++) {
                                      binary += String.fromCharCode(bytes[i]);
                                  }
                                  // One JSON object rather than a delimited string: the label is
                                  // somebody else's text and may carry any delimiter a hand-rolled
                                  // shape would pick.
                                  return JSON.stringify({
                                      mediaType: (response.headers.get('content-type') || 'image/jpeg')
                                          .split(';')[0],
                                      label,
                                      data: btoa(binary)
                                  });
                              }
                          } catch { /* fall through to the canvas */ }
                      }

                      // Cross-origin, which is the ordinary case: a page's pictures usually come
                      // from a CDN on another host. A scripted fetch there is subject to CORS and
                      // image CDNs send no Access-Control-Allow-Origin -- they serve <img>, not
                      // XHR -- so fetch fails on exactly the images the page is displaying
                      // perfectly well. Read the pixels the browser already has instead.
                      //
                      // This re-encodes rather than passing the served bytes through, which is the
                      // price of reaching them at all: a canvas has pixels, not a file.
                      try {
                          const drawn = await new Promise((resolve) => {
                              const probe = new Image();
                              probe.crossOrigin = 'anonymous';
                              probe.onload = () => resolve(probe);
                              probe.onerror = () => resolve(null);
                              // Already in the page and decoded: this is a cache hit, not a
                              // second download, whenever the element itself has loaded.
                              probe.src = url;
                          });

                          const source = drawn
                              || (img.complete && img.naturalWidth > 0 ? img : null);
                          if (!source) return 'error';

                          const canvas = document.createElement('canvas');
                          canvas.width = source.naturalWidth;
                          canvas.height = source.naturalHeight;
                          canvas.getContext('2d').drawImage(source, 0, 0);

                          // Throws a SecurityError on a tainted canvas -- an image the browser
                          // will show but not let script read. That is a real refusal.
                          const dataUrl = canvas.toDataURL('image/png');
                          return JSON.stringify({
                              mediaType: 'image/png',
                              label,
                              data: dataUrl.split(',')[1]
                          });
                      } catch {
                          return 'error';
                      }
                  }
                  """,
                imageRef);

            return ImageFetchPayload.Parse(imageRef, payload);
        }
        catch (PlaywrightException)
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused);
        }
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _sessions.CloseAsync(sessionId);
    }

    public async Task ClearCookiesAsync()
    {
        if (_context == null)
        {
            return;
        }

        try
        {
            await _context.ClearCookiesAsync();
        }
        catch (PlaywrightException)
        {
            // Context may be closed, ignore
        }
    }

    // Runs a browser operation, and if the connection turns out to be dead (the Camoufox
    // WebSocket dropped between calls or mid-flight), force-reconnects and runs it once more
    // on a fresh context/page. Without this the first call after any drop is sacrificed and
    // only the *next* call self-heals, surfacing "Target page, context or browser has been
    // closed" to the caller.
    private async Task<T> ExecuteWithReconnectAsync<T>(Func<Task<T>> operation)
    {
        var generation = await EnsureInitializedAsync();
        try
        {
            return await operation();
        }
        catch (Exception ex) when (IsConnectionClosed(ex))
        {
            // A "page/context/browser has been closed" error is definitive proof the connection is
            // unusable — more reliable than IBrowser.IsConnected, which can still report true when
            // only the page or context died. Force a reconnect of the generation we just used, under
            // _initLock; if a concurrent caller already replaced it, this no-ops and we retry on the
            // fresh connection instead of tearing it down.
            await EnsureConnectionAsync(replaceGeneration: generation);
            return await operation();
        }
    }

    private static bool IsConnectionClosed(Exception ex) =>
        ex is PlaywrightException &&
        (ex.Message.Contains("has been closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Connection closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Browser closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("WebSocket", StringComparison.OrdinalIgnoreCase));

    internal Task<long> EnsureInitializedAsync() => EnsureConnectionAsync(replaceGeneration: null);

    // replaceGeneration: when set, the caller's connection (that generation) failed mid-operation, so
    // replace it even though IsConnectionHealthy() may still report true — a closed page/context can
    // leave IBrowser.IsConnected == true. If another caller already advanced the generation, the
    // connection has already been replaced and this returns the current one without reconnecting.
    private async Task<long> EnsureConnectionAsync(long? replaceGeneration)
    {
        if (replaceGeneration is null && IsConnectionHealthy())
        {
            return Volatile.Read(ref _connectionGeneration);
        }

        await _initLock.WaitAsync();
        try
        {
            if (replaceGeneration is { } stale && _connectionGeneration != stale)
            {
                return _connectionGeneration;
            }

            if (replaceGeneration is null && IsConnectionHealthy())
            {
                return _connectionGeneration;
            }

            // A previous connection died (Camoufox restart, idle drop, network blip) or a closed
            // page/context was detected mid-operation. Tear down the stale browser/context/sessions
            // before reconnecting, otherwise every call would keep failing with "Target page, context
            // or browser has been closed" until the process restarts.
            if (_initialized)
            {
                await ResetStaleConnectionAsync();
            }

            _browser = await ConnectBrowserAsync();

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                Locale = "en-US",
                TimezoneId = "America/New_York",
                HasTouch = false,
                IsMobile = false,
                JavaScriptEnabled = true,
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept-Language"] = "en-US,en;q=0.9"
                }
            });

            _context.SetDefaultTimeout(DefaultOperationTimeoutMs);

            _initialized = true;
            return ++_connectionGeneration;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private bool IsConnectionHealthy() => _initialized && _browser?.IsConnected == true;

    private async Task ResetStaleConnectionAsync()
    {
        _initialized = false;
        _sessions.Clear();

        try
        {
            if (_context is not null)
            {
                await _context.CloseAsync();
            }

            if (_browser is not null)
            {
                await _browser.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
            // Best-effort: the connection is already gone, closing it may throw.
        }
        finally
        {
            _context = null;
            _browser = null;
        }
    }

    private async Task<IBrowser> ConnectBrowserAsync()
    {
        if (browserFactory is not null)
        {
            return await browserFactory();
        }

        if (string.IsNullOrEmpty(wsEndpoint))
        {
            throw new InvalidOperationException(
                "Camoufox WebSocket endpoint is not configured. Set the CAMOUFOX__WSENDPOINT environment variable.");
        }

        _playwright ??= await Playwright.CreateAsync();

        for (var attempt = 1; attempt <= ConnectionRetryAttempts; attempt++)
        {
            try
            {
                return await _playwright.Firefox.ConnectAsync(wsEndpoint);
            }
            catch (PlaywrightException) when (attempt < ConnectionRetryAttempts)
            {
                await Task.Delay(ConnectionRetryDelayMs);
            }
        }

        throw new InvalidOperationException(
            $"Failed to connect to Camoufox at {wsEndpoint} after {ConnectionRetryAttempts} attempts.");
    }

    private static async Task ScrollToLoadAsync(IPage page, int scrollSteps, CancellationToken ct)
    {
        for (var i = 1; i <= scrollSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            await page.EvaluateAsync($"() => window.scrollTo(0, document.body.scrollHeight * {i} / {scrollSteps})");
            await Task.Delay(200, ct);
        }

        await page.EvaluateAsync("() => window.scrollTo(0, 0)");
    }

    // The measure-and-strip step on a session's own page. Internal rather than public: it is the
    // one part of the catalogue that needs a real browser to be worth testing, and nothing outside
    // this assembly and its tests has any business running it out of band.
    internal async Task AnnotateImagesOnSessionAsync(string sessionId)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found");
        await StripDomNoiseAsync(session.Page);
    }

    private static async Task StripDomNoiseAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync($$"""
                () => {
                    const MIN_IMAGE_SIDE = {{PageImageEntry.MinRenderedSide}};
                    // Measure every image as the reader would have seen it, then keep the address
                    // only for the ones big enough to be about something. Markup width/height is
                    // never consulted: it lies as often as it is absent, and a stylesheet-sized
                    // image carries neither. One pass over the whole page, so measuring an
                    // image-heavy page costs no round trip per image.
                    //
                    // This runs before anything below removes a node, and must stay there: with
                    // <style> stripped first, a stylesheet-sized image measures at its intrinsic
                    // size -- zero for one whose bytes have not arrived -- and every CSS-sized
                    // content image on the page filters out as furniture.
                    //
                    // getBoundingClientRect is read for every image before any attribute is
                    // written, because writing into the DOM between reads forces a reflow per
                    // image -- the difference between one layout pass and hundreds.
                    const images = Array.from(document.querySelectorAll('img'));
                    const boxes = images.map(img => {
                        const rect = img.getBoundingClientRect();
                        return {
                            w: Math.round(rect.width || img.naturalWidth || 0),
                            h: Math.round(rect.height || img.naturalHeight || 0)
                        };
                    });

                    // Numbered over survivors only, in document order, so the ref the entry writes
                    // is the ref this page answers to. The counter lives here rather than in the
                    // extractor because the fetch resolves against this DOM, not against the text.
                    let imageRefCounter = 0;

                    images.forEach((img, i) => {
                        const { w, h } = boxes[i];
                        const survives = w >= MIN_IMAGE_SIDE && h >= MIN_IMAGE_SIDE;

                        if (survives) {
                            imageRefCounter++;
                            img.setAttribute('data-img-w', String(w));
                            img.setAttribute('data-img-h', String(h));
                            img.setAttribute('data-img-ref', 'i-' + imageRefCounter);
                        } else {
                            img.removeAttribute('data-img-ref');
                        }

                        // The address is stripped for everything that did not survive, as before:
                        // long CDN URLs bloat the markdown and buy nothing. A survivor keeps its
                        // src so the fetch can resolve the ref against it.
                        if (!survives) {
                            img.removeAttribute('src');
                        }

                        img.removeAttribute('srcset');
                        img.removeAttribute('data-src');
                        img.removeAttribute('data-image');
                        img.removeAttribute('data-mediabook');
                        img.removeAttribute('data-path');
                    });

                    // Remove script/style/noscript — they contribute nothing to text content
                    document.querySelectorAll('script, style, noscript').forEach(el => el.remove());

                    // Remove iframes (ads, trackers) — they contribute no text content
                    document.querySelectorAll('iframe').forEach(el => el.remove());

                    // Remove SVGs — icons/graphics that bloat HTML without adding text value
                    document.querySelectorAll('svg').forEach(el => el.remove());

                    // Compact link hrefs: strip tracking params and shorten same-site URLs
                    const currentHost = location.hostname;
                    document.querySelectorAll('a[href]').forEach(a => {
                        try {
                            const url = new URL(a.href);
                            ['utm_source','utm_medium','utm_campaign','utm_content','utm_term',
                             'ats','atc','ata','ref','cid','frm','noc'].forEach(p => url.searchParams.delete(p));
                            if (url.hostname === currentHost || url.hostname === 'www.' + currentHost) {
                                a.setAttribute('href', url.pathname + url.search + url.hash);
                            } else {
                                a.setAttribute('href', url.toString());
                            }
                        } catch { /* skip malformed URLs */ }
                    });
                }
            """);
        }
        catch
        {
            // DOM cleanup is best-effort — page content can still be processed without it
        }
    }

    // Ramped so an already-settled page is not made to wait as long as one that is still rendering.
    // Two consecutive identical reads at 100ms and 400ms span the same 400ms of page time the old
    // fixed 200ms schedule needed three sleeps (600ms) to cover. The total is held at the same
    // 1200ms ceiling as before on purpose: a longer tail was measured costing 2.4s on pages that
    // never settle at all (elmundo.es, github.com), where extra patience buys nothing because the
    // churn is ads and carousels rather than content still arriving.
    private static readonly int[] _stabilityIntervalsMs = [100, 300, 400, 400];

    // Element count plus text length, rather than the serialised document the loop used to compare.
    // Two reasons. It is far cheaper — a ~20-byte reply instead of up to 2MB per check, measured at
    // 824ms of pure transfer on a 1.8MB page — and it is a better signal, because it tracks what is
    // actually extracted. Comparing whole HTML meant a rotating spinner's inline style or a class
    // toggle counted as "still rendering", so ad-heavy pages never settled and always paid the cap
    // for content that had finished arriving long before.
    //
    // documentElement, not body: body is null on XML and frameset documents, and an evaluate that
    // dereferenced it would throw, be caught as a PlaywrightException, and turn a working browse
    // into an error result.
    private const string DomFingerprintScript =
        """
        () => {
            const root = document.documentElement;
            if (!root) return '0:0';
            return root.getElementsByTagName('*').length + ':' + (root.textContent || '').length;
        }
        """;

    // Polls the page until it stops changing (catches client-side rendering after DOMContentLoaded).
    // The fingerprint is a single cheap evaluate, so the sleeps are the only real cost — which is why
    // the first reading is taken immediately, as the baseline to compare against, rather than after a
    // sleep that bought nothing.
    internal static async Task WaitForDomStabilityAsync(
        IPage page,
        CancellationToken ct = default,
        int stableCountRequired = 2,
        IReadOnlyList<int>? intervalsMs = null)
    {
        var intervals = intervalsMs ?? _stabilityIntervalsMs;
        var previousHtml = await page.EvaluateAsync<string>(DomFingerprintScript);
        var stableCount = 0;

        foreach (var interval in intervals)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(interval, ct);
            var currentHtml = await page.EvaluateAsync<string>(DomFingerprintScript);

            if (currentHtml == previousHtml)
            {
                stableCount++;
                if (stableCount >= stableCountRequired)
                {
                    return;
                }
            }
            else
            {
                stableCount = 0;
            }

            previousHtml = currentHtml;
        }
    }

    internal static IReadOnlyList<StructuredData> ExtractStructuredData(string html)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(html,
            @"<script[^>]*type=[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return matches
            .SelectMany(match =>
            {
                try
                {
                    var json = match.Groups[1].Value.Trim();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("@graph", out var graph) &&
                        graph.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        // ToList() inside the try: Select() alone is lazy, so an exception raised
                        // per-item would otherwise surface at the outer ToList(), long after this
                        // catch went out of scope, and fail the whole browse.
                        return graph.EnumerateArray()
                            .Select(item => new StructuredData(ReadType(item), item.GetRawText()))
                            .ToList()
                            .AsEnumerable();
                    }

                    return new[] { new StructuredData(ReadType(root), json) }.AsEnumerable();
                }
                catch
                {
                    return Enumerable.Empty<StructuredData>();
                }
            })
            .ToList();
    }

    // JSON-LD lets "@type" be a string or an array of strings; product pages ship both. Reading it
    // as a bare string throws on the array form, so take the first string the array offers.
    private static string ReadType(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("@type", out var type))
        {
            return "Unknown";
        }

        return type.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => type.GetString() ?? "Unknown",
            System.Text.Json.JsonValueKind.Array => type.EnumerateArray()
                .Where(t => t.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(t => t.GetString())
                .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "Unknown",
            _ => "Unknown"
        };
    }

    // Gecko reports a refused connection, a bot wall and a dead domain with equally opaque
    // NS_ERROR_* codes, and the raw text arrives wrapped in a multi-line Playwright call log.
    // Passing that through told the agent nothing about whether retrying was worth it, so in
    // production it re-browsed URLs that would never load. Each code keeps its own advice.
    internal static string DescribeNavigationError(string playwrightMessage)
    {
        var code = playwrightMessage.Split('\n')[0].Trim();

        if (code.Contains("NS_ERROR_NET_ERROR_RESPONSE", StringComparison.Ordinal))
        {
            return "The site refused the request, which usually means it is blocking automated "
                   + "browsers. Retrying will not help; use web_search to find the same "
                   + "information on a site that allows it.";
        }

        if (code.Contains("NS_ERROR_REDIRECT_LOOP", StringComparison.Ordinal))
        {
            return "The site sent the browser round a redirect loop, which usually means it is "
                   + "blocking automated browsers or wants a cookie the session does not carry. "
                   + "Retrying will not help; try web_search for another source.";
        }

        if (code.Contains("NS_ERROR_UNKNOWN_HOST", StringComparison.Ordinal))
        {
            return "That domain does not resolve, so the address is wrong or the site no longer "
                   + "exists. Check the URL, or use web_search to find the current one.";
        }

        if (code.Contains("NS_ERROR_CONNECTION_REFUSED", StringComparison.Ordinal))
        {
            return "The server refused the connection, so it is down or not serving this port. "
                   + "It may be worth trying again later.";
        }

        if (code.Contains("NS_ERROR_NET_TIMEOUT", StringComparison.Ordinal))
        {
            return "The server did not respond in time. It may be slow or temporarily down; "
                   + "trying again later may work.";
        }

        return $"Browser error: {playwrightMessage}";
    }

    private static bool ValidateUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
    }

    private static BrowseResult CreateErrorResult(string sessionId, string url, string message)
    {
        return new BrowseResult(
            SessionId: sessionId,
            Url: url,
            Status: BrowseStatus.Error,
            Title: null,
            Content: null,
            ContentLength: 0,
            Truncated: false,
            Metadata: null,
            StructuredData: null,
            DismissedModals: null,
            ErrorMessage: message
        );
    }

    // DataDome serves its loader as dd.js, but matching that as a bare substring also matches any
    // content-hashed bundle whose name ends in "dd" — github.com ships
    // marketing-navigation-ece1017251744ddd.js — which reported CaptchaRequired and returned no
    // content for the whole site. Anchoring on a path or attribute boundary keeps the detection and
    // drops the collision. ("geo.captcha-delivery.com" is subsumed by "captcha-delivery.com".)
    private static readonly Regex _dataDomeLoaderPattern =
        new("""[/"'=]dd\.js\b""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool ContainsCaptcha(string html)
    {
        return html.Contains("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("datadome", StringComparison.OrdinalIgnoreCase) ||
               _dataDomeLoaderPattern.IsMatch(html);
    }

    private async Task<(bool Solved, string? Message)> TrySolveCaptchaAsync(
        IPage page,
        string websiteUrl,
        string html,
        CancellationToken ct)
    {
        if (captchaSolver == null)
        {
            return (false, "CAPTCHA detected but no solver configured");
        }

        var captchaUrl = ExtractCaptchaUrl(html);
        if (string.IsNullOrEmpty(captchaUrl))
        {
            return (false, "CAPTCHA detected but could not extract CAPTCHA URL");
        }

        // Retrieve the actual UA from the active page (Camoufox generates its own)
        var userAgent = await page.EvaluateAsync<string>("navigator.userAgent");

        var request = new DataDomeCaptchaRequest(
            WebsiteUrl: websiteUrl,
            CaptchaUrl: captchaUrl,
            UserAgent: userAgent
        );

        var solution = await captchaSolver.SolveDataDomeAsync(request, ct);

        if (!solution.Success || string.IsNullOrEmpty(solution.Cookie))
        {
            return (false, solution.ErrorMessage ?? "CAPTCHA solving failed");
        }

        await SetDataDomeCookieAsync(websiteUrl, solution.Cookie);
        return (true, "CAPTCHA solved successfully");
    }

    private static string? ExtractCaptchaUrl(string html)
    {
        var patterns = new[]
        {
            "src=\"(https://geo\\.captcha-delivery\\.com/[^\"]+)\"",
            @"src='(https://geo\.captcha-delivery\.com/[^']+)'",
            "(https://geo\\.captcha-delivery\\.com/captcha/[^\\s\"'<>]+)"
        };

        return patterns
            .Select(pattern => Regex.Match(html, pattern))
            .Where(match => match is { Success: true, Groups.Count: > 1 })
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault();
    }

    private async Task SetDataDomeCookieAsync(string websiteUrl, string cookieValue)
    {
        var uri = new Uri(websiteUrl);

        // Parse the cookie string (format: "datadome=value")
        var cookieParts = cookieValue.Split('=', 2);
        var cookieName = cookieParts.Length > 0 ? cookieParts[0] : "datadome";
        var cookieVal = cookieParts.Length > 1 ? cookieParts[1] : cookieValue;

        await _context!.AddCookiesAsync(
        [
            new Cookie
            {
                Name = cookieName,
                Value = cookieVal,
                Domain = uri.Host,
                Path = "/",
                Secure = uri.Scheme == "https",
                HttpOnly = true
            }
        ]);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();

        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}