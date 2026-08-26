using System.Diagnostics;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class PlaywrightWebBrowser(
    ICaptchaSolver? captchaSolver = null,
    string? wsEndpoint = null,
    Func<Task<IBrowser>>? browserFactory = null,
    int tabCap = BrowserSessionManager.DefaultTabCap,
    TimeSpan? idleTimeout = null)
    : IWebBrowser, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly BrowserSessionManager _sessions = new(
        idleTimeout: idleTimeout ?? TimeSpan.FromMinutes(30),
        pruneInterval: TimeSpan.FromMinutes(5),
        tabCap: tabCap);
    private readonly ModalDismisser _modalDismisser = new();
    private readonly AccessibilitySnapshotService _snapshotService = new();
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
            // Why: the Camoufox WebSocket can drop mid-navigation. ExecuteWithReconnectAsync
            // reconnects and re-runs the navigation on a fresh page instead of leaking
            // "Target page, context or browser has been closed" to the caller. Serialization is
            // per tab, inside NavigateOnceAsync — browses of different URLs run in parallel.
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
        // The pre-navigation jitter and acquiring the tab are independent, so the delay runs
        // alongside page creation (measured ~150ms) instead of after it. A random gap still precedes
        // GotoAsync — it just no longer costs anything on top of work already happening.
        var jitter = Task.Delay(Random.Shared.Next(50, 150), ct);

        // Same-tab work queues; another tab's navigation runs beside this one untouched. The lock
        // spans the whole read — navigation through extraction — so nothing answers off a
        // half-replaced document.
        var (tab, tabLock) = await AcquireLockedTabAsync(request, ct);
        using var _ = tabLock;
        var page = tab.Page;

        await jitter;

        var navigationTimedOut = false;
        IResponse? response = null;

        try
        {
            response = await page.GotoAsync(request.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Commit,
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

        // The commit says what arrived. An HTML document goes on to DOMContentLoaded as before;
        // a URL that answers with the picture itself renders the browser's image-viewer document,
        // and Camoufox's juggler never reports DOMContentLoaded for one — that wait burned its
        // whole timeout on an event that cannot come and then called the page incomplete. An
        // image document waits for its one image to decode instead.
        var committedType = response?.Headers.GetValueOrDefault("content-type")?.Split(';')[0].Trim();
        if (!navigationTimedOut)
        {
            try
            {
                if (committedType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) is true)
                {
                    await page.WaitForFunctionAsync(
                        "() => { const i = document.querySelector('img'); return !!i && i.complete && i.naturalWidth > 0; }",
                        null,
                        new() { Timeout = 30000 });
                }
                else
                {
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 30000 });
                }
            }
            catch (TimeoutException)
            {
                navigationTimedOut = true;
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                navigationTimedOut = true;
            }
        }

        // The navigation replaced whatever document the tab held, so every ref stamped there is
        // superseded; the annotation below then registers the fresh image range.
        _sessions.MarkTabNavigated(request.SessionId, tab);
        _sessions.NoteFinalUrl(request.SessionId, tab, page.Url);

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

        if (request.ScrollToLoad)
        {
            await ScrollToLoadAsync(page, request.ScrollSteps, ct);
        }

        await WaitForDomStabilityAsync(page, ct: ct);

        // Strip hidden overlays, dismissed modals, and non-content noise from DOM to prevent them
        // from consuming the content budget during HTML processing. After the scroll and the
        // stability wait, so images the scroll lazy-loaded are measured and stamped rather than
        // left unlisted on exactly the pages scrollToLoad targets.
        await AnnotateAndStripAsync(request.SessionId, tab);

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
            ImagesBeyondWindow = processed.ImagesBeyondWindow,
            NextOffset = processed.NextOffset
        };
    }

    // A tab can be evicted between being acquired and being locked — its closed page would then
    // throw "Target closed" mid-navigation, which reads as the whole connection dying and tears
    // down every session. Re-acquire instead: the pool drops the dead tab and opens a fresh one.
    private async Task<(BrowserTab Tab, IDisposable Lock)> AcquireLockedTabAsync(
        BrowseRequest request, CancellationToken ct)
    {
        // Bounded: each retry is a fresh acquisition, which at the cap evicts another tab — an
        // unbounded spin under adversarial concurrency would churn tabs whose refs the model
        // holds. Three misses in a row is a session replacing tabs faster than a browse can hold
        // one, and saying so beats joining the churn.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var acquisition = await _sessions.AcquireTabForBrowseAsync(
                request.SessionId, request.Url, _context!, ct);
            var tabLock = await _sessions.LockTabAsync(acquisition.Tab, ct);
            if (!acquisition.Tab.Page.IsClosed)
            {
                return (acquisition.Tab, tabLock);
            }

            tabLock.Dispose();
        }

        throw new InvalidOperationException(
            "The session is replacing tabs faster than this browse can hold one. Try again.");
    }

    public async Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
    {
        var tab = _sessions.Get(sessionId)?.CurrentTab;
        if (tab == null)
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
            using var tabLock = await _sessions.LockTabAsync(tab, ct);
            var html = await tab.Page.ContentAsync();
            var request = new BrowseRequest(SessionId: sessionId, Url: tab.FinalUrl);
            var processed = await HtmlProcessor.ProcessAsync(request, html, ct);

            return new BrowseResult(
                SessionId: sessionId,
                Url: tab.Page.Url,
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
                ImagesBeyondWindow = processed.ImagesBeyondWindow,
                NextOffset = processed.NextOffset
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResult(sessionId, tab.FinalUrl, $"Error: {ex.Message}");
        }
    }

    public async Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
    {
        // A snapshot naming its page reads that page's tab; only the ref-less, page-less form
        // falls to the tab last touched — which a parallel browse may have moved.
        var session = _sessions.Get(request.SessionId);
        if (session is null)
        {
            return new SnapshotResult(request.SessionId, null, null, 0, "Session not found. Use web_browse first.");
        }

        BrowserTab? tab;
        if (request.ForUrl is { } url)
        {
            // The named page already gone — evicted, or its tab reused by another browse — must
            // refuse rather than fall back to the current tab: that would attach another page's
            // refs to this page's text, the silent wrong-target answer the name exists to prevent.
            tab = session.FindByUrl(url);
            if (tab is null)
            {
                return new SnapshotResult(request.SessionId, null, null, 0,
                    $"The page at {url} is no longer open in this session. Browse it again for fresh refs.");
            }
        }
        else
        {
            tab = session.CurrentTab;
        }

        if (tab == null || tab.Page.IsClosed)
        {
            return new SnapshotResult(request.SessionId, null, null, 0, "Session not found. Use web_browse first.");
        }

        try
        {
            // A snapshot racing a navigation on this tab waits rather than reading a
            // half-replaced DOM.
            using var tabLock = await _sessions.LockTabAsync(tab, ct);

            // Evicted while this call queued: the tab is gone, the session is not.
            if (tab.Page.IsClosed)
            {
                return new SnapshotResult(request.SessionId, null, null, 0,
                    $"That tab has been closed. Browse {tab.RequestedUrl} again for fresh refs.");
            }

            // Re-navigated while this call queued: the tab no longer shows the named page.
            if (request.ForUrl is { } named && tab.RequestedUrl != named && tab.FinalUrl != named)
            {
                return new SnapshotResult(request.SessionId, null, null, 0,
                    $"The page at {named} is no longer open in this session. Browse it again for fresh refs.");
            }

            _sessions.TouchTab(request.SessionId, tab);
            var result = await CaptureNumberedSnapshotAsync(
                request.SessionId, tab, request.Selector);
            return new SnapshotResult(request.SessionId, tab.Page.Url, result.Snapshot, result.RefCount, null);
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
            return new SnapshotResult(request.SessionId, tab.Page.Url, null, 0, ex.Message);
        }
    }

    public async Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.SessionNotFound,
                null, false, null, null, "Session not found. Use web_browse first.");
        }

        // A ref acts on the tab that stamped it, whichever that is; only the ref-less back falls
        // to the tab last touched. A stale ref refuses through its wall before any DOM is asked.
        BrowserTab? tab;
        if (request.Action == WebActionType.Back || string.IsNullOrEmpty(request.Ref))
        {
            tab = session.CurrentTab;
        }
        else
        {
            switch (_sessions.RouteRef(request.SessionId, request.Ref))
            {
                case RefRouting.Routed routed:
                    tab = routed.Tab;
                    break;
                case RefRouting.Superseded superseded:
                    return new WebActionResult(request.SessionId, WebActionStatus.RefSuperseded,
                        null, false, null, null, null, RefUrl: superseded.Url);
                case RefRouting.Closed closed:
                    return new WebActionResult(request.SessionId, WebActionStatus.RefClosed,
                        null, false, null, null, null, RefUrl: closed.Url);
                default:
                    // Never issued: fall to the current tab, whose DOM answers not-found.
                    tab = session.CurrentTab;
                    break;
            }
        }

        if (tab == null)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.SessionNotFound,
                null, false, null, null, "Session not found. Use web_browse first.");
        }

        // A closed page under a live session is that one tab gone — evicted, or closed by the
        // site — not the session dead: its wall names the page to browse again.
        if (tab.Page.IsClosed)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.RefClosed,
                null, false, null, null, null, RefUrl: tab.RequestedUrl);
        }

        var page = tab.Page;

        try
        {
            // Why: an action can trigger navigation; serialize it against other work on this one
            // tab so it cannot interrupt (or be interrupted by) a concurrent navigation there.
            using var tabLock = await _sessions.LockTabAsync(tab, ct);

            // Evicted while this call queued on its lock: a closed page is the closed-ref wall,
            // not a lost session — the browser connection is fine and the other tabs live on.
            if (tab.Page.IsClosed)
            {
                return new WebActionResult(request.SessionId, WebActionStatus.RefClosed,
                    null, false, null, null, null, RefUrl: tab.RequestedUrl);
            }

            _sessions.TouchTab(request.SessionId, tab);

            // Read under the lock: a navigation that was queued ahead of this action moved the
            // tab, and a before-URL captured outside the queue would compare against a page that
            // is no longer there.
            var urlBefore = page.Url;

            return request.Action switch
            {
                WebActionType.Back => await ExecuteBackAsync(request, tab, urlBefore, ct),
                _ => await ExecuteElementActionAsync(request, tab, urlBefore, ct)
            };
        }
        catch (PlaywrightException ex) when (IsConnectionClosed(ex))
        {
            // A closed page with the browser still up is this one tab evicted mid-call, not the
            // connection dying — the session's other tabs live on and must not be dropped.
            if (tab.Page.IsClosed)
            {
                return new WebActionResult(request.SessionId, WebActionStatus.RefClosed,
                    null, false, null, null, null, RefUrl: tab.RequestedUrl);
            }

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
        WebActionRequest request, BrowserTab tab, string urlBefore, CancellationToken ct)
    {
        var page = tab.Page;
        if (string.IsNullOrEmpty(request.Ref))
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                page.Url, false, null, null, $"ref is required for {request.Action} action.");
        }

        // A popup left over from an earlier, unrelated call on this tab must not divert this
        // action's answer.
        _sessions.TakePendingPopup(request.SessionId, tab);

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

        // A click that opened a new tab answers from where the site actually took the model —
        // the popup's URL, content and freshly stamped refs — not a stale diff of the page left
        // behind. A popup already evicted answers nothing; the action itself still succeeded, so
        // it falls through to the ordinary diff of the page it acted on.
        if (_sessions.TakePendingPopup(request.SessionId, tab) is { } popupTab
            && await AnswerFromPopupAsync(request.SessionId, popupTab) is { } popupAnswer)
        {
            return popupAnswer;
        }

        var navigationOccurred = page.Url != urlBefore;
        if (navigationOccurred)
        {
            // The document was replaced: every ref stamped on the old one is superseded before
            // the fresh snapshot below registers its own range.
            _sessions.MarkTabNavigated(request.SessionId, tab);
        }

        _sessions.NoteFinalUrl(request.SessionId, tab, page.Url);

        // Same document: the refs the model holds stay valid, and only elements the action
        // surfaced get fresh numbers. A navigation restamps the new document from scratch.
        var after = await CaptureNumberedSnapshotAsync(
            request.SessionId, tab, null, augment: !navigationOccurred);

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

    internal static string StripRefs(string line)
        => System.Text.RegularExpressions.Regex.Replace(line, @"\s*\[ref=e-\d+\]", "");

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

    private async Task<WebActionResult?> AnswerFromPopupAsync(string sessionId, BrowserTab popupTab)
    {
        // The popup's own lock, held on top of the acting tab's: the first read of an adopted
        // page keeps the same no-half-replaced-DOM promise as any other tab. A popup is always a
        // fresh page, never an existing tab, so the nesting cannot cycle.
        using var popupLock = await _sessions.LockTabAsync(popupTab);

        // Evicted before it could answer — reading it would throw "Target closed", which upstream
        // reads as the whole connection dying. Null lets the action answer from its own tab.
        if (popupTab.Page.IsClosed)
        {
            return null;
        }

        try
        {
            await popupTab.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                new() { Timeout = 10_000 });
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            // A slow popup still answers with whatever has rendered; the snapshot says the rest.
        }

        _sessions.NoteFinalUrl(sessionId, popupTab, popupTab.Page.Url);
        var snapshot = await CaptureNumberedSnapshotAsync(sessionId, popupTab, null);

        return new WebActionResult(sessionId, WebActionStatus.Success,
            popupTab.Page.Url, true, snapshot.Snapshot, null, null);
    }

    private async Task<WebActionResult> ExecuteBackAsync(
        WebActionRequest request, BrowserTab tab, string urlBefore, CancellationToken ct)
    {
        var page = tab.Page;
        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // A back with no history navigates nothing: the document is unchanged, so the refs on it
        // — the images' included — are not superseded by a page that never moved.
        if (page.Url != urlBefore)
        {
            _sessions.MarkTabNavigated(request.SessionId, tab);
        }

        _sessions.NoteFinalUrl(request.SessionId, tab, page.Url);
        var snapshot = await CaptureNumberedSnapshotAsync(request.SessionId, tab, null);

        return new WebActionResult(request.SessionId, WebActionStatus.Success,
            page.Url, page.Url != urlBefore, snapshot.Snapshot, null, null);
    }

    // A numbered capture under the session's element-stamp lease, so the numbers written into
    // the page continue from where the session stopped and are never issued twice. The committed
    // range is registered against the tab, which is what routes each ref back here later.
    // Restamping (the default) renumbers the whole document and supersedes the tab's earlier
    // ranges; augmenting keeps every ref already on the document — a non-navigating action stamps
    // only what appeared, so the refs the model is mid-flow with keep resolving. The before-diff
    // capture (preserveRefs) writes no numbers and needs no lease.
    private async Task<AccessibilitySnapshotService.SnapshotCaptureResult> CaptureNumberedSnapshotAsync(
        string sessionId, BrowserTab tab, string? selector, bool augment = false)
    {
        using var stamp = await _sessions.BeginStampAsync(sessionId, RefNamespace.Element, tab);
        var result = await _snapshotService.CaptureAsync(
            tab.Page, selector, sessionId, startNumber: stamp.Start, augmentRefs: augment);
        stamp.Commit(result.RefCount, supersede: !augment);
        return result;
    }


    public async Task EvaluateOnSessionAsync(string sessionId, string script)
    {
        var tab = _sessions.Get(sessionId)?.CurrentTab
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found");
        await tab.Page.EvaluateAsync(script);
    }

    // The reading half of the escape hatch: what the page answers, for a test that has to ask the
    // DOM what a step left behind rather than infer it from the markdown. Internal for the same
    // reason its void sibling is public only by history -- nothing outside needs it.
    internal async Task<T> EvaluateOnSessionAsync<T>(string sessionId, string script)
    {
        var tab = _sessions.Get(sessionId)?.CurrentTab
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found");
        return await tab.Page.EvaluateAsync<T>(script);
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
        if (session?.CurrentTab is null)
        {
            return new ImageFetchResult(request.SessionId, [], SessionMissing: true);
        }

        // Sequential rather than a LINQ projection over tasks: these are real fetches out of one
        // context, and firing eight at once buys nothing while making a rate-limiting site read
        // the burst as exactly what it is. Each ref fetches through the tab that stamped it —
        // refs from two tabs answer from two pages in one call — and each routed fetch is a
        // touch, so peeking at an old tab's picture refocuses that tab.
        var images = new List<FetchedImage>(request.Refs.Count);
        foreach (var imageRef in request.Refs)
        {
            ct.ThrowIfCancellationRequested();
            switch (_sessions.RouteRef(request.SessionId, imageRef))
            {
                case RefRouting.Routed routed:
                    _sessions.TouchTab(request.SessionId, routed.Tab);
                    images.Add(await FetchLockedAsync(routed.Tab, imageRef, ct));
                    break;
                case RefRouting.Superseded superseded:
                    images.Add(new FetchedImage(imageRef, null, null,
                        ImageFetchStatus.RefSuperseded, Url: superseded.Url));
                    break;
                case RefRouting.Closed closed:
                    images.Add(new FetchedImage(imageRef, null, null,
                        ImageFetchStatus.RefClosed, Url: closed.Url));
                    break;
                default:
                    // Never issued: the current tab answers not-found, as it always has.
                    images.Add(await FetchLockedAsync(session.CurrentTab, imageRef, ct));
                    break;
            }
        }

        return new ImageFetchResult(request.SessionId, images);
    }

    // A fetch racing a navigation on the same tab waits its turn rather than resolving the ref
    // against a half-replaced document.
    private async Task<FetchedImage> FetchLockedAsync(BrowserTab tab, string imageRef, CancellationToken ct)
    {
        using var tabLock = await _sessions.LockTabAsync(tab, ct);

        // Evicted while this fetch queued on its lock: the closed-ref wall, not a site refusal.
        if (tab.Page.IsClosed)
        {
            return new FetchedImage(imageRef, null, null,
                ImageFetchStatus.RefClosed, Url: tab.RequestedUrl);
        }

        var fetched = await FetchOneAsync(tab.Page, imageRef);

        // An eviction that lands mid-fetch surfaces as a Playwright error; the closed page says
        // which wall it truly was.
        return fetched.Status == ImageFetchStatus.SiteRefused && tab.Page.IsClosed
            ? new FetchedImage(imageRef, null, null, ImageFetchStatus.RefClosed, Url: tab.RequestedUrl)
            : fetched;
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

                      // The same ladder the entry's label came from, rung for rung — a blank rung
                      // falls through instead of short-circuiting the chain to "", dimensions
                      // close it, and the entry's own sanitizing (whitespace flattened, brackets
                      // rounded) is applied the same way. Diverging here leaves the note calling
                      // the picture different words than the entry the model chose it by.
                      const nonEmpty = (t) => t && t.trim() ? t.trim() : null;
                      const fileName = /^data:/i.test(src)
                          ? ''
                          : (src.split(/[?#]/)[0].split('/').pop() || '').trim();
                      const spoken = nonEmpty(img.getAttribute('alt'))
                          || nonEmpty(img.closest('figure')?.querySelector('figcaption')?.textContent)
                          || nonEmpty(img.getAttribute('title'))
                          || nonEmpty(img.closest('a')?.textContent)
                          || (/^[^\s/]+\.[A-Za-z0-9]{1,5}$/.test(fileName) ? fileName : null)
                          || `${img.getAttribute('{{PageImageEntry.WidthAttribute}}') || 0}x${img.getAttribute('{{PageImageEntry.HeightAttribute}}') || 0}`;
                      const flat = spoken.replace(/\s+/g, ' ')
                          .replace(/\[/g, '(').replace(/\]/g, ')').trim();
                      // The entry's own cut, spelled the same (PageImageEntry.Sanitize): a bare
                      // slice read as the tool breaking mid-word, and the entry and the note must
                      // name one picture with one set of words.
                      const label = flat.length <= 500
                          ? flat
                          : flat.slice(0, 500).trimEnd() + '…';

                      const url = new URL(src, location.href).toString();

                      // The wire fetch keeps the bytes exactly as served, so no re-encoding and
                      // no loss — but only for a raster the chat wire accepts. Anything else (an
                      // SVG site logo was the live case) falls through to the canvas and leaves
                      // as PNG; as-served SVG bytes made the vision provider refuse the whole
                      // request with a 400. Same-origin sends the page's own credentials;
                      // cross-origin goes anonymous, because image CDNs answer ACAO * and a
                      // wildcard is exactly what a credentialed request is rejected against —
                      // the canvas used to answer the ACAO'd case first and re-encoded every
                      // shared jpeg as a fatter PNG.
                      const wireFetch = async (credentials) => {
                          try {
                              const response = await fetch(url, { credentials });
                              const mediaType = (response.headers.get('content-type') || 'image/jpeg')
                                  .split(';')[0];
                              const wireRasters =
                                  ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];
                              if (response.ok && wireRasters.includes(mediaType)) {
                                  const bytes = new Uint8Array(await response.arrayBuffer());
                                  let binary = '';
                                  for (let i = 0; i < bytes.length; i++) {
                                      binary += String.fromCharCode(bytes[i]);
                                  }
                                  // One JSON object rather than a delimited string: the label is
                                  // somebody else's text and may carry any delimiter a hand-rolled
                                  // shape would pick.
                                  return JSON.stringify({
                                      mediaType,
                                      label,
                                      data: btoa(binary)
                                  });
                              }
                          } catch { /* fall through to the canvas */ }
                          return null;
                      };

                      const sameOrigin = new URL(url).origin === location.origin;
                      const served = await wireFetch(sameOrigin ? 'include' : 'omit');
                      if (served) return served;

                      // Cross-origin, which is the ordinary case: a page's pictures usually come
                      // from a CDN on another host. What fails there is a *credentialed* request:
                      // image CDNs send Access-Control-Allow-Origin: *, and a wildcard is exactly
                      // what credentials: 'include' is rejected against. So the image is loaded
                      // anonymously and read off a canvas -- gated by the same ACAO header an
                      // anonymous fetch would be, but reusing the pixels the browser has already
                      // decoded. The canvas holds pixels rather than a file, so this re-encodes
                      // as PNG; that is the choice's cost, not its purpose.
                      //
                      // A CDN sending no ACAO at all fails the anonymous probe, and the fallback
                      // to the rendered element taints the canvas: a cookie-gated cross-origin
                      // image refuses even though the page shows it. Accepted (docs/adr/0033).
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
                          // No probe result and no decoded element either: the bytes never
                          // arrived at all — a dead link, not a CDN guarding its pixels.
                          if (!source) return 'never-loaded';

                          // A source with no intrinsic size — a viewBox-only SVG reports
                          // naturalWidth 0 — would size the canvas 0x0 and leave as a zero-byte
                          // "success" the vision provider rejects whole. The pixels are on
                          // screen, so hand it to the rungs below the canvas instead.
                          if (!source.naturalWidth || !source.naturalHeight) {
                              return JSON.stringify({ tainted: true, label, url });
                          }

                          const canvas = document.createElement('canvas');
                          canvas.width = source.naturalWidth;
                          canvas.height = source.naturalHeight;
                          canvas.getContext('2d').drawImage(source, 0, 0);

                          // Throws a SecurityError on a tainted canvas -- an image the browser
                          // will show but not let script read. The pixels are on screen all the
                          // same, so that answer goes back for a screenshot rather than a refusal.
                          const dataUrl = canvas.toDataURL('image/png');
                          return JSON.stringify({
                              mediaType: 'image/png',
                              label,
                              data: dataUrl.split(',')[1]
                          });
                      } catch (e) {
                          return e && e.name === 'SecurityError'
                              ? JSON.stringify({ tainted: true, label, url })
                              : 'error';
                      }
                  }
                  """,
                imageRef);

            if (ImageFetchPayload.Tainted(payload, out var taintedLabel, out var taintedUrl))
            {
                return await FetchAsServedAsync(page, imageRef, taintedLabel, taintedUrl)
                    ?? await ScreenshotAsync(page, imageRef, taintedLabel);
            }

            return ImageFetchPayload.Parse(imageRef, payload);
        }
        catch (PlaywrightException)
        {
            return new FetchedImage(imageRef, null, null, ImageFetchStatus.SiteRefused);
        }
    }

    // CORS binds script inside the page and nothing else: the context's own request client pulls
    // the same address with the same cookies and keeps the bytes exactly as served — the JPL
    // gallery, whose CDN sends no ACAO header at all, is the live page this bought back. Null
    // hands the miss to the screenshot rung rather than deciding refusal here.
    private static async Task<FetchedImage?> FetchAsServedAsync(
        IPage page, string imageRef, string? label, string? url)
    {
        if (url is null)
        {
            return null;
        }

        try
        {
            var response = await page.Context.APIRequest.GetAsync(url, new() { Timeout = 10_000 });
            try
            {
                var mediaType = response.Headers.GetValueOrDefault("content-type")?.Split(';')[0].Trim();
                if (!response.Ok || mediaType is null || !WireRasters.Contains(mediaType))
                {
                    return null;
                }

                var bytes = await response.BodyAsync();
                return new FetchedImage(imageRef, mediaType, bytes, ImageFetchStatus.Success, Label: label);
            }
            finally
            {
                // Playwright retains the body until the response is disposed or its context dies,
                // and this context lives until reconnect — undisposed, every re-request kept its
                // full image in memory for the life of the process.
                await response.DisposeAsync();
            }
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    // The rasters the chat wire accepts — the same list the fetch script keeps for the
    // same-origin branch. Anything else leaves through the canvas or the screenshot as PNG.
    private static readonly string[] WireRasters =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    // The last rung under a tainted canvas: the compositor already painted these pixels, and a
    // screenshot of the element reads them back without CORS having a say. What leaves is the
    // rendered box re-encoded as PNG at the size the page shows it.
    private static async Task<FetchedImage> ScreenshotAsync(IPage page, string imageRef, string? label)
    {
        try
        {
            var bytes = await page.Locator($"[{PageImageEntry.RefAttribute}='{imageRef}']")
                .ScreenshotAsync(new() { Type = ScreenshotType.Png, Timeout = 10_000 });
            return new FetchedImage(imageRef, "image/png", bytes, ImageFetchStatus.Success, Label: label);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
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

    // The pool as the policy layer sees it, for integration tests that assert on which tabs are
    // alive rather than inferring it from tool results. Nothing in production reads this.
    internal BrowserSessionManager Sessions => _sessions;

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
        var tab = _sessions.Get(sessionId)?.CurrentTab
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found");
        await AnnotateAndStripAsync(sessionId, tab);
    }

    // The measure-and-strip pass under the session's image-stamp lease: refs stamped here continue
    // from where the session stopped, so no picture ever answers to a number an earlier page used.
    private async Task AnnotateAndStripAsync(string sessionId, BrowserTab tab)
    {
        using var stamp = await _sessions.BeginStampAsync(sessionId, RefNamespace.Image, tab);
        var count = await StripDomNoiseAsync(tab.Page, stamp.Start);
        stamp.Commit(count);
    }

    private static async Task<int> StripDomNoiseAsync(IPage page, int startImageNumber)
    {
        try
        {
            return await page.EvaluateAsync<int>($$"""
                (startNumber) => {
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
                    // It starts where the session left off, so a number stamped here was never
                    // stamped by an earlier page or pass.
                    let stampedCount = 0;

                    images.forEach((img, i) => {
                        const { w, h } = boxes[i];
                        const survives = w >= MIN_IMAGE_SIDE && h >= MIN_IMAGE_SIDE;

                        if (survives) {
                            stampedCount++;
                            img.setAttribute('data-img-w', String(w));
                            img.setAttribute('data-img-h', String(h));
                            img.setAttribute('data-img-ref', 'i-' + (startNumber + stampedCount - 1));
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

                    return stampedCount;
                }
            """, startImageNumber);
        }
        catch
        {
            // DOM cleanup is best-effort — page content can still be processed without it
            return 0;
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

        try
        {
            if (_context != null)
            {
                await _context.CloseAsync();
            }

            if (_browser != null)
            {
                await _browser.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
            // A Camoufox connection already dead at shutdown has nothing left to close.
        }

        _playwright?.Dispose();
        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}