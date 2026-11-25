using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Apps;
using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace McpServerApps.Checks;

// A check: a built app's preview opened in this server's own headless Chromium, as the person whose turn
// ran `./check`, at each viewport asked, and what it saw written where the mount shows it
// (`/<slug>/.checks/latest/`): one screenshot per viewport and a report of the console, failed requests,
// content-policy violations, an accessibility snapshot and the app's own assertion files' outcomes.
//
// - **The browser reaches the app at this server**, never through public DNS: the apps domain resolves
//   to the pages listener (`--host-resolver-rules`), and the page arrives by Host as any app origin's does.
// - **The preview is a secure context**, as it is behind the proxy on https: its `__Host-` session cookie
//   and its worker need one, and plain http under a real domain is none, so the browser is told to treat
//   that one origin as secure. Only full Chromium honours that (the headless shell ignores it), and the
//   flag is the process's, so each check launches a browser of its own; closing it is how the deadline
//   stops everything at once.
// - **It reads as the person whose turn ran it**: an app session this server opens itself for the
//   preview (AppSessions.OpenCheck), its reads carrying the call's own token to the agent, ended with the
//   check.
// - **Nothing of the app runs outside the page.** Assertion files are read here as text and evaluated
//   in the page, where the app's own code runs; the page reaches only its origin, the CDNs and its
//   manifest's `connect` origins — anything else, a navigation onto the deployment's network included, is
//   aborted and reported.
// - **Checks past a quota are refused, not queued**; one past its deadline is stopped and says so.
public sealed class AppChecks(AppsSettings settings, AppSessions sessions, BuiltAppStore store, TimeProvider clock, ILogger<AppChecks> logger)
{
    public const string LatestDirectory = "latest";
    public const string ReportFile = "report.json";

    // A phone and a desktop, when a check names none.
    public static readonly IReadOnlyList<CheckViewport> DefaultViewports = [new(390, 844), new(1280, 800)];

    private const int MaxEntries = 200;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _byHousehold = new(StringComparer.Ordinal);
    private int _running;

    // How many checks are running now, every household's.
    internal int Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public async Task<CheckOutcome> RunAsync(BuiltApp app, CheckRequest request, CancellationToken ct)
    {
        if (Admit(app.HouseholdId) is { } refused)
        {
            return refused;
        }

        try
        {
            return new CheckOutcome.Ran(await CheckAsync(app, request, ct));
        }
        finally
        {
            Release(app.HouseholdId);
        }
    }

    private CheckOutcome.Refused? Admit(string household)
    {
        var limits = settings.Checks;
        lock (_gate)
        {
            var mine = _byHousehold.GetValueOrDefault(household);
            if (mine >= limits.PerHousehold)
            {
                return new CheckOutcome.Refused("concurrent checks per household", limits.PerHousehold);
            }

            if (_running >= limits.Total)
            {
                return new CheckOutcome.Refused("concurrent checks on this server", limits.Total);
            }

            _byHousehold[household] = mine + 1;
            _running++;
            return null;
        }
    }

    private void Release(string household)
    {
        lock (_gate)
        {
            _running--;
            if (--_byHousehold[household] == 0)
            {
                _byHousehold.Remove(household);
            }
        }
    }

    private async Task<CheckReport> CheckAsync(BuiltApp app, CheckRequest request, CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var limits = settings.Checks;
        var host = $"{app.Slug}-{app.Id}{AppOrigin.PreviewSuffix}.{settings.AppsDomain}";
        var address = $"http://{host}";
        var checks = store.ChecksDirectory(app);
        var run = Path.Combine(checks, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(run);
        var session = sessions.OpenCheck(app.Author, new AppOrigin(app.Id, Preview: true), request.CallToken, started + limits.Deadline);
        var reachable = new HashSet<string>([address, .. settings.Cdns, .. request.Manifest.Connect], StringComparer.OrdinalIgnoreCase);
        using var deadline = new CancellationTokenSource(limits.Deadline, clock);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var viewports = new List<ViewportRun>();
        string? failure = null;
        try
        {
            using var playwright = await Playwright.CreateAsync().WaitAsync(stopped.Token);
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                // Full Chromium in its new headless mode: the headless shell ignores the secure-origin flag.
                Channel = "chromium",
                Headless = true,
                Timeout = (float)limits.Deadline.TotalMilliseconds,
                Args =
                [
                    $"--host-resolver-rules=MAP *.{settings.AppsDomain} 127.0.0.1:{settings.Listeners.Pages}",
                    $"--unsafely-treat-insecure-origin-as-secure={address}"
                ]
            });
            await using var closing = stopped.Token.Register(() => _ = browser.CloseAsync());
            foreach (var viewport in request.Viewports)
            {
                var seen = new ViewportRun(viewport);
                viewports.Add(seen);
                await ViewAsync(browser, seen, address + request.Path, session, reachable, request.Assertions, run, stopped.Token);
            }
        }
        catch (Exception ex) when (stopped.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation(ex, "A check of app {AppId} was stopped at its deadline of {Deadline}", app.Id, limits.Deadline);
        }
        catch (PlaywrightException ex)
        {
            logger.LogWarning(ex, "A check of app {AppId} could not run its browser", app.Id);
            failure = $"the browser could not run the check: {ex.Message}";
        }
        finally
        {
            sessions.End(session);
            if (ct.IsCancellationRequested)
            {
                Directory.Delete(run, recursive: true);
            }
        }

        var report = new CheckReport
        {
            App = app.Slug,
            Origin = $"https://{host}",
            Path = request.Path,
            StartedAt = started,
            FinishedAt = clock.GetUtcNow(),
            Deadline = limits.Deadline,
            DeadlineHit = deadline.IsCancellationRequested,
            Error = failure,
            Viewports = [.. viewports.Select(v => v.Report())]
        };
        await File.WriteAllTextAsync(Path.Combine(run, ReportFile), JsonSerializer.Serialize(report, _json), CancellationToken.None);
        Publish(checks, run);
        return report;
    }

    // One viewport in a context of its own: the session's cookie, the page opened and let settle, then
    // shown, read for structure, and asked its assertions.
    private async Task ViewAsync(
        IBrowser browser, ViewportRun seen, string url, string session, IReadOnlySet<string> reachable,
        IReadOnlyList<CheckAssertion> assertions, string run, CancellationToken stopped)
    {
        var viewport = seen.Viewport;
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height },
            DeviceScaleFactor = 1,
            ServiceWorkers = ServiceWorkerPolicy.Allow
        });
        await context.RouteAsync("**/*", route =>
            reachable.Contains(OriginOf(route.Request.Url)) ? route.ContinueAsync() : route.AbortAsync("blockedbyclient"));
        await context.ExposeFunctionAsync<string>(ViolationBinding, json =>
        {
            if (JsonSerializer.Deserialize<PolicyViolation>(json, _json) is { } violation)
            {
                seen.Violations.Enqueue(violation);
            }
        });
        await context.AddInitScriptAsync(ViolationListener);
        var page = await context.NewPageAsync();
        page.Console += (_, message) => seen.Console.Enqueue(new ConsoleEntry(message.Type, message.Text, message.Location));
        page.PageError += (_, error) => seen.Console.Enqueue(new ConsoleEntry("uncaught", error, null));
        page.RequestFailed += (_, failed) => seen.Failed.Enqueue(new FailedRequest(failed.Method, failed.Url, null,
            failed.Failure is { } why && why.Contains("ERR_BLOCKED_BY_CLIENT", StringComparison.Ordinal)
                ? "blocked: a check's page reaches only its own origin, the CDNs and its manifest's connect origins"
                : failed.Failure));
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                seen.Failed.Enqueue(new FailedRequest(response.Request.Method, response.Url, response.Status, null));
            }
        };

        // Host-only and Secure, as the app sign-in sets it: through the browser's own protocol, since
        // Playwright's cookie call makes a domain cookie, which a `__Host-` name refuses.
        var cdp = await context.NewCDPSessionAsync(page);
        await cdp.SendAsync("Network.setCookie", new Dictionary<string, object>
        {
            ["name"] = AppSessions.CookieName, ["value"] = session, ["url"] = url, ["path"] = "/",
            ["secure"] = true, ["httpOnly"] = true, ["sameSite"] = "Lax"
        });

        try
        {
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 0 });
        }
        catch (PlaywrightException ex) when (!stopped.IsCancellationRequested)
        {
            seen.Error = ex.Message;
        }

        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = (float)settings.Checks.Settle.TotalMilliseconds
            });
            seen.Settled = true;
        }
        catch (TimeoutException)
        {
            seen.Settled = false;
        }

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(run, viewport.Screenshot), Timeout = 0 });
        seen.Screenshot = viewport.Screenshot;
        seen.Accessibility = await page.Locator("body").AriaSnapshotAsync(new LocatorAriaSnapshotOptions { Timeout = 0 });
        foreach (var assertion in assertions)
        {
            seen.Assertions.Add(await AssertAsync(page, assertion, stopped));
        }
    }

    // An assertion file is the body of an async function run in the page: it passes when it returns (what
    // it returns is its message) and fails with what it throws. One that does not parse fails with why; one
    // cut short by the deadline is no outcome at all.
    private static async Task<AssertionOutcome> AssertAsync(IPage page, CheckAssertion assertion, CancellationToken stopped)
    {
        try
        {
            var outcome = await page.EvaluateAsync<JsonElement>(
                $$"""
                  (async () => {
                    try {
                      const value = await (async () => {
                  {{assertion.Source}}
                      })();
                      return { passed: true, message: value === undefined || value === null ? '' : String(value) };
                    } catch (error) {
                      return { passed: false, message: error instanceof Error ? error.message : String(error) };
                    }
                  })()
                  """);
            return new AssertionOutcome(assertion.File, outcome.GetProperty("passed").GetBoolean(),
                outcome.GetProperty("message").GetString() ?? "");
        }
        catch (PlaywrightException ex) when (!stopped.IsCancellationRequested)
        {
            return new AssertionOutcome(assertion.File, false, ex.Message);
        }
    }

    // The finished run becomes what the mount shows, whole: the last run's files never mix with this one's.
    private static void Publish(string checks, string run)
    {
        var latest = Path.Combine(checks, LatestDirectory);
        var retired = Path.Combine(checks, $"retired-{Guid.NewGuid():N}");
        if (Directory.Exists(latest))
        {
            Directory.Move(latest, retired);
        }

        Directory.Move(run, latest);
        if (Directory.Exists(retired))
        {
            Directory.Delete(retired, recursive: true);
        }
    }

    private static string OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : "";

    private const string ViolationBinding = "__zigguratCheckPolicyViolation";

    // Every content-policy refusal the document meets, handed to the check as it happens.
    private const string ViolationListener =
        $$"""
          document.addEventListener('securitypolicyviolation', e => globalThis.{{ViolationBinding}}(JSON.stringify({
            directive: e.effectiveDirective, blockedUri: e.blockedURI || null,
            sourceFile: e.sourceFile || null, line: e.lineNumber || null, sample: e.sample || null
          })));
          """;

    // What one viewport saw, gathered from the page's events as they come.
    private sealed class ViewportRun(CheckViewport viewport)
    {
        public CheckViewport Viewport => viewport;
        public ConcurrentQueue<ConsoleEntry> Console { get; } = new();
        public ConcurrentQueue<FailedRequest> Failed { get; } = new();
        public ConcurrentQueue<PolicyViolation> Violations { get; } = new();
        public List<AssertionOutcome> Assertions { get; } = [];
        public string? Screenshot { get; set; }
        public string? Accessibility { get; set; }
        public bool Settled { get; set; }
        public string? Error { get; set; }

        public ViewportReport Report() => new()
        {
            Name = viewport.Name,
            Width = viewport.Width,
            Height = viewport.Height,
            Screenshot = Screenshot,
            Settled = Settled,
            Error = Error,
            Console = [.. Console.Take(MaxEntries)],
            FailedRequests = [.. Failed.Take(MaxEntries)],
            PolicyViolations = [.. Violations.Take(MaxEntries)],
            Accessibility = Accessibility,
            Assertions = [.. Assertions]
        };
    }
}

public sealed record CheckViewport(int Width, int Height)
{
    public string Name => $"{Width}x{Height}";
    public string Screenshot => $"{Name}.png";
}

// One assertion file of the app's `checks/`, by its path in the app, and its text.
public sealed record CheckAssertion(string File, string Source);

// What to check: the route opened, at each viewport, the manifest it is served under, the app's assertion
// files, and the token of the call that asked, which the page reads with.
public sealed record CheckRequest(
    IReadOnlyList<CheckViewport> Viewports, string Path, AppManifest Manifest, IReadOnlyList<CheckAssertion> Assertions,
    string CallToken);

public abstract record CheckOutcome
{
    public sealed record Ran(CheckReport Report) : CheckOutcome;

    public sealed record Refused(string Limit, int Value) : CheckOutcome;
}

public sealed record CheckReport
{
    public required string App { get; init; }

    // The preview's address as a person opens it, through the proxy.
    public required string Origin { get; init; }
    public required string Path { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required TimeSpan Deadline { get; init; }

    // Stopped there: a viewport the check did not finish has only what it saw by then, or none.
    public required bool DeadlineHit { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<ViewportReport> Viewports { get; init; }
}

public sealed record ViewportReport
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string? Screenshot { get; init; }

    // Whether the network went quiet within Checks:Settle before the page was shown.
    public required bool Settled { get; init; }
    public string? Error { get; init; }
    public required IReadOnlyList<ConsoleEntry> Console { get; init; }
    public required IReadOnlyList<FailedRequest> FailedRequests { get; init; }
    public required IReadOnlyList<PolicyViolation> PolicyViolations { get; init; }
    public string? Accessibility { get; init; }
    public required IReadOnlyList<AssertionOutcome> Assertions { get; init; }
}

public sealed record ConsoleEntry(string Type, string Text, string? Location);

// A request that failed outright (Error) or was answered 400 or worse (Status).
public sealed record FailedRequest(string Method, string Url, int? Status, string? Error);

public sealed record PolicyViolation(string Directive, string? BlockedUri, string? SourceFile, int? Line, string? Sample);

public sealed record AssertionOutcome(string File, bool Passed, string Message);