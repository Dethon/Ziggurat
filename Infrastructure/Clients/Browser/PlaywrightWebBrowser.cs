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

        var outcome = await _sessions.BrowseAsync(request.SessionId, request.Url, _context!,
            async ctx =>
            {
                await jitter;
                return await BrowsePageAsync(request, ctx, ct);
            }, ct);

        return outcome switch
        {
            TabOutcome<BrowseResult>.Ran ran => ran.Result,
            TabOutcome<BrowseResult>.Closed closed => CreateErrorResult(request.SessionId, closed.Url,
                "The page's tab was closed while it was being read. Browse it again."),
            TabOutcome<BrowseResult>.AcquireExhausted => CreateErrorResult(request.SessionId, request.Url,
                "The session is replacing tabs faster than this browse can hold one. Try again."),
            _ => CreateErrorResult(request.SessionId, request.Url,
                $"Unexpected tab outcome: {outcome.GetType().Name}")
        };
    }

    private async Task<BrowseResult> BrowsePageAsync(
        BrowseRequest request, TabWorkContext ctx, CancellationToken ct)
    {
        var page = ctx.Page;
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
        await ctx.StampAsync(start => StripDomNoiseAsync(page, start));

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

    public async Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var outcome = await _sessions.OnCurrentTabAsync(sessionId, StampPolicy.None,
                async ctx =>
                {
                    var html = await ctx.Page.ContentAsync();
                    var request = new BrowseRequest(SessionId: sessionId, Url: ctx.UrlBefore);
                    var processed = await HtmlProcessor.ProcessAsync(request, html, ct);

                    return new BrowseResult(
                        SessionId: sessionId,
                        Url: ctx.Page.Url,
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
                }, ct);

            return outcome switch
            {
                TabOutcome<BrowseResult>.Ran ran => ran.Result,
                TabOutcome<BrowseResult>.Closed closed => CreateErrorResult(sessionId, closed.Url,
                    $"That tab has been closed. Browse {closed.Url} again."),
                _ => new BrowseResult(sessionId, "", BrowseStatus.SessionNotFound,
                    null, null, 0, false, null, null, null, "No active browser session found")
            };
        }
        catch (Exception ex)
        {
            return CreateErrorResult(sessionId, "", $"Error: {ex.Message}");
        }
    }

    public async Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
    {
        try
        {
            // A snapshot naming its page reads that page's tab; only the ref-less, page-less form
            // falls to the tab last touched — which a parallel browse may have moved.
            Task<SnapshotResult> Capture(TabWorkContext ctx) => CaptureSnapshotAsync(request, ctx);
            var outcome = request.ForUrl is { } url
                ? await _sessions.OnUrlAsync(
                    request.SessionId, url, StampPolicy.Restamp(RefNamespace.Element), Capture, ct)
                : await _sessions.OnCurrentTabAsync(
                    request.SessionId, StampPolicy.Restamp(RefNamespace.Element), Capture, ct);

            return outcome switch
            {
                TabOutcome<SnapshotResult>.Ran ran => ran.Result,
                TabOutcome<SnapshotResult>.Closed closed => new SnapshotResult(
                    request.SessionId, null, null, 0,
                    $"That tab has been closed. Browse {closed.Url} again for fresh refs."),
                TabOutcome<SnapshotResult>.AddressedTabGone gone => new SnapshotResult(
                    request.SessionId, null, null, 0,
                    $"The page at {gone.Url} is no longer open in this session. Browse it again for fresh refs."),
                _ => new SnapshotResult(request.SessionId, null, null, 0,
                    "Session not found. Use web_browse first.")
            };
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
            return new SnapshotResult(request.SessionId, null, null, 0, ex.Message);
        }
    }

    private async Task<SnapshotResult> CaptureSnapshotAsync(SnapshotRequest request, TabWorkContext ctx)
    {
        AccessibilitySnapshotService.SnapshotCaptureResult? captured = null;
        await ctx.StampAsync(async start =>
        {
            captured = await _snapshotService.CaptureAsync(
                ctx.Page, request.Selector, request.SessionId,
                startNumber: start, augmentRefs: ctx.AugmentRefs);
            return captured.RefCount;
        });

        return new SnapshotResult(
            request.SessionId, ctx.Page.Url, captured!.Snapshot, captured.RefCount, null);
    }

    public async Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
    {
        try
        {
            // A ref acts on the tab that stamped it, whichever that is; only the ref-less back
            // falls to the tab last touched. A stale ref refuses through its wall before any DOM
            // is asked; each wall maps to its action status here, once.
            var outcome = request.Action == WebActionType.Back || string.IsNullOrEmpty(request.Ref)
                ? await _sessions.OnCurrentTabAsync(
                    request.SessionId, StampPolicy.Restamp(RefNamespace.Element),
                    ctx => ExecuteBackAsync(request, ctx), ct)
                : await ExecuteElementActionAsync(request, ct);

            return outcome switch
            {
                TabOutcome<WebActionResult>.Ran ran => ran.Result,
                TabOutcome<WebActionResult>.Superseded superseded => new WebActionResult(
                    request.SessionId, WebActionStatus.RefSuperseded,
                    null, false, null, null, null, RefUrl: superseded.Url),
                TabOutcome<WebActionResult>.Closed closed => new WebActionResult(
                    request.SessionId, WebActionStatus.RefClosed,
                    null, false, null, null, null, RefUrl: closed.Url),
                _ => new WebActionResult(request.SessionId, WebActionStatus.SessionNotFound,
                    null, false, null, null, "Session not found. Use web_browse first.")
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
                null, false, null, null, "Operation timed out.");
        }
        catch (PlaywrightException ex)
        {
            var status = ex.Message.Contains("not found") || ex.Message.Contains("no element")
                ? WebActionStatus.ElementNotFound
                : WebActionStatus.Error;
            return new WebActionResult(request.SessionId, status,
                null, false, null, null, ex.Message);
        }
        catch (Exception ex)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                null, false, null, null, ex.Message);
        }
    }

    private async Task<TabOutcome<WebActionResult>> ExecuteElementActionAsync(
        WebActionRequest request, CancellationToken ct)
    {
        Dictionary<string, int>? beforeLines = null;
        return await _sessions.OnRefAsync(
            request.SessionId, request.Ref!,
            StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            act: async ctx =>
            {
                var acted = await ActOnElementAsync(request, ctx, ct);
                beforeLines = acted.BeforeLines;
                return acted.ShortCircuit;
            },
            answer: ctx => AnswerFromActedTabAsync(request, ctx, beforeLines!),
            popup: new PopupAnswer<WebActionResult>(
                StampPolicy.Restamp(RefNamespace.Element),
                ctx => AnswerFromPopupAsync(request.SessionId, ctx)),
            ct);
    }

    // Null ShortCircuit means the gesture landed and the answer phase decides what the model
    // sees; a non-null one is a refusal decided before or during the act.
    private sealed record ElementActOutcome(
        WebActionResult? ShortCircuit, Dictionary<string, int>? BeforeLines);

    private async Task<ElementActOutcome> ActOnElementAsync(
        WebActionRequest request, TabWorkContext ctx, CancellationToken ct)
    {
        var page = ctx.Page;
        try
        {
            var locator = AccessibilitySnapshotService.ResolveRef(page, request.Ref!);
            try
            {
                await locator.WaitForAsync(
                    new()
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = BrowserSessionManager.RefResolutionTimeoutMs
                    });
            }
            catch (TimeoutException)
            {
                // A ref that is not on the page is stale, not slow — the page re-rendered, or a
                // browse replaced the document, and no amount of extra waiting brings it back.
                // Fail fast and name the recovery instead of burning the operation timeout.
                return new ElementActOutcome(new WebActionResult(
                    request.SessionId, WebActionStatus.ElementNotFound,
                    page.Url, false, null, null,
                    $"Element {request.Ref} is no longer on the page — the page has changed since the " +
                    "snapshot was taken. Call web_snapshot to get current refs, then retry."), null);
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
                        return new ElementActOutcome(new WebActionResult(
                            request.SessionId, WebActionStatus.Error,
                            page.Url, false, null, null, "endRef is required for drag action."), null);
                    }

                    await locator.DragToAsync(AccessibilitySnapshotService.ResolveRef(page, request.EndRef));
                    break;
                default:
                    return new ElementActOutcome(new WebActionResult(
                        request.SessionId, WebActionStatus.Error,
                        page.Url, false, null, null, $"Unhandled element action: {request.Action}"), null);
            }

            if (request.WaitForNavigation)
            {
                try
                {
                    await page.WaitForURLAsync(url => url != ctx.UrlBefore,
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

            return new ElementActOutcome(null, beforeLines);
        }
        catch (Exception ex) when (BrowserSessionManager.IsConnectionClosed(ex))
        {
            // The module tells this tab's death apart from the connection's.
            throw;
        }
        catch (TimeoutException)
        {
            return new ElementActOutcome(new WebActionResult(
                request.SessionId, WebActionStatus.Timeout,
                page.Url, false, null, null, "Operation timed out."), null);
        }
        catch (PlaywrightException ex)
        {
            var status = ex.Message.Contains("not found") || ex.Message.Contains("no element")
                ? WebActionStatus.ElementNotFound
                : WebActionStatus.Error;
            return new ElementActOutcome(new WebActionResult(
                request.SessionId, status,
                page.Url, false, null, null, ex.Message), null);
        }
    }

    private async Task<WebActionResult> AnswerFromActedTabAsync(
        WebActionRequest request, TabWorkContext ctx, Dictionary<string, int> beforeLines)
    {
        var page = ctx.Page;
        var navigationOccurred = page.Url != ctx.UrlBefore;

        // Same document: the refs the model holds stay valid, and only elements the action
        // surfaced get fresh numbers. A navigation restamps the new document from scratch —
        // ctx.AugmentRefs answers that question once for the capture and the lease alike.
        var after = await CaptureNumberedAsync(request.SessionId, ctx, null);

        // On navigation the entire page changed — return the new snapshot directly
        // instead of a diff that would duplicate all content as removed + added lines
        var snapshot = navigationOccurred
            ? after
            : BuildSnapshotDiff(beforeLines, after, request.Ref!, request.Action);

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

    // A click that opened a new tab answers from where the site actually took the model — the
    // popup's URL, content and freshly stamped refs. The module owns the pickup timing and the
    // nested lock; this is only the page choreography of reading an adopted page.
    private async Task<WebActionResult> AnswerFromPopupAsync(string sessionId, TabWorkContext ctx)
    {
        try
        {
            await ctx.Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                new() { Timeout = 10_000 });
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            // A slow popup still answers with whatever has rendered; the snapshot says the rest.
        }

        var snapshot = await CaptureNumberedAsync(sessionId, ctx, null);

        return new WebActionResult(sessionId, WebActionStatus.Success,
            ctx.Page.Url, true, snapshot, null, null);
    }

    private async Task<WebActionResult> ExecuteBackAsync(WebActionRequest request, TabWorkContext ctx)
    {
        var page = ctx.Page;
        if (request.Action != WebActionType.Back)
        {
            return new WebActionResult(request.SessionId, WebActionStatus.Error,
                page.Url, false, null, null, $"ref is required for {request.Action} action.");
        }

        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // A back with no history navigates nothing: the module's supersede rule sees the unmoved
        // URL and leaves the tab's other handles alone; the restamp below still renumbers the
        // elements.
        var snapshot = await CaptureNumberedAsync(request.SessionId, ctx, null);

        return new WebActionResult(request.SessionId, WebActionStatus.Success,
            page.Url, page.Url != ctx.UrlBefore, snapshot, null, null);
    }

    // A numbered capture through the module-held lease: the script starts where the session
    // stopped and reports how many refs it wrote; the module commits the range with the
    // supersede flag the intent decided.
    private async Task<string> CaptureNumberedAsync(string sessionId, TabWorkContext ctx, string? selector)
    {
        var snapshot = "";
        await ctx.StampAsync(async start =>
        {
            var result = await _snapshotService.CaptureAsync(
                ctx.Page, selector, sessionId, startNumber: start, augmentRefs: ctx.AugmentRefs);
            snapshot = result.Snapshot;
            return result.RefCount;
        });
        return snapshot;
    }

    public async Task EvaluateOnSessionAsync(string sessionId, string script)
    {
        await EvaluateOnSessionAsync<object?>(sessionId, script);
    }

    // The reading half of the escape hatch: what the page answers, for a test that has to arrange
    // a page or ask the DOM what a step left behind. Internal for the same reason its void sibling
    // is public only by history -- nothing outside needs it. Rides the current-tab intent so even
    // the escape hatch performs no session bookkeeping of its own.
    internal async Task<T> EvaluateOnSessionAsync<T>(string sessionId, string script)
    {
        var outcome = await _sessions.OnCurrentTabAsync(sessionId, StampPolicy.None,
            ctx => ctx.Page.EvaluateAsync<T>(script));
        return outcome is TabOutcome<T>.Ran ran
            ? ran.Result
            : throw new InvalidOperationException($"Session '{sessionId}' not found");
    }

    // The arranging half of the escape hatch: lets a test answer a fake CDN address from inside
    // the session's page without reaching into the pool for the page handle.
    // The context-level arranging half: answers an address before any page asks for it, so a
    // hermetic test can navigate to a route-fulfilled page instead of borrowing a live third
    // party as its anchor. Routes die with the context on reconnect; register before navigating.
    internal async Task RouteOnContextAsync(string url, Func<IRoute, Task> handler)
    {
        await EnsureInitializedAsync();
        await _context!.RouteAsync(url, handler);
    }

    internal async Task RouteOnSessionAsync(string sessionId, string url, Func<IRoute, Task> handler)
    {
        var outcome = await _sessions.OnCurrentTabAsync(sessionId, StampPolicy.None,
            async ctx =>
            {
                await ctx.Page.RouteAsync(url, handler);
                return true;
            });
        if (outcome is not TabOutcome<bool>.Ran)
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found");
        }
    }

    // The fetch goes through the page that listed the picture. Camoufox holds the cookies, the
    // referer and the fingerprint, and a great many images are served only to a request carrying
    // them -- a bare HttpClient would answer 403 or a placeholder pixel on exactly the pages where
    // this stack is earning its keep. It is also what makes the ref meaningful: it resolves
    // against the live DOM, so no URL has to enter the model's context to be usable.
    public async Task<ImageFetchResult> FetchImagesAsync(
        ImageFetchRequest request, CancellationToken ct = default)
    {
        if (request.Refs.Count == 0)
        {
            return new ImageFetchResult(request.SessionId, [],
                SessionMissing: _sessions.Get(request.SessionId) is null);
        }

        // Sequential rather than a LINQ projection over tasks: these are real fetches out of one
        // context, and firing eight at once buys nothing while making a rate-limiting site read
        // the burst as exactly what it is. One routed run per ref — no batch semantics: each ref
        // fetches through the tab that stamped it, each ref's wall falls out per image, and each
        // routed fetch is a touch, so peeking at an old tab's picture refocuses that tab.
        var images = new List<FetchedImage>(request.Refs.Count);
        foreach (var imageRef in request.Refs)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _sessions.OnRefAsync(request.SessionId, imageRef, StampPolicy.None,
                ctx => FetchOneAsync(ctx.Page, imageRef), ct);

            switch (outcome)
            {
                case TabOutcome<FetchedImage>.Ran ran:
                    images.Add(ran.Result);
                    break;
                case TabOutcome<FetchedImage>.Superseded superseded:
                    images.Add(new FetchedImage(imageRef, null, null,
                        ImageFetchStatus.RefSuperseded, Url: superseded.Url));
                    break;
                case TabOutcome<FetchedImage>.Closed closed:
                    images.Add(new FetchedImage(imageRef, null, null,
                        ImageFetchStatus.RefClosed, Url: closed.Url));
                    break;
                default:
                    // No session, or none of its tabs left to answer from: the whole request is
                    // missing its session, as it always was.
                    return new ImageFetchResult(request.SessionId, [], SessionMissing: true);
            }
        }

        return new ImageFetchResult(request.SessionId, images);
    }

    // The whole descent runs against the probe for this page, inside the locked tab work
    // callback the caller already holds -- the page cannot change between rungs. The label the
    // answer carries comes from the one ladder, over the facts the locate rung observed.
    private static async Task<FetchedImage> FetchOneAsync(IPage page, string imageRef)
    {
        try
        {
            return await ImageAcquisition.FetchAsync(new PlaywrightImageProbe(page), imageRef);
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

    // Pool lifecycle, surfaced for tests that assert the idle policy through the contract without
    // waiting out the real prune timer. Production pruning runs on the interval timer alone.
    internal Task PruneIdleAsync() => _sessions.PruneIdleAsync();

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
        BrowserSessionManager.IsConnectionClosed(ex);

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
        var outcome = await _sessions.OnCurrentTabAsync(
            sessionId, StampPolicy.Restamp(RefNamespace.Image),
            async ctx =>
            {
                await ctx.StampAsync(start => StripDomNoiseAsync(ctx.Page, start));
                return true;
            });

        if (outcome is not TabOutcome<bool>.Ran)
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found");
        }
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