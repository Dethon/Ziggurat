using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Apps;
using Domain.Contracts;
using Domain.Tools;
using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace McpServerApps.Checks;

// A check: a built app's preview opened in a headless Chromium of its own (CheckBrowser: launched here,
// or in the deployment on the browser server in the `apps-browser` container), as the person whose turn
// ran `./check`, at each viewport asked, and what it saw written where the mount shows it
// (`/<slug>/.checks/latest/`): one screenshot per viewport and a report of the console, failed requests,
// content-policy violations, an accessibility snapshot and the app's own assertion files' outcomes.
//
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

    // The home screen icon as it installs, at full size and at launcher size.
    public static readonly IReadOnlyList<int> IconSizes = [512, 64];

    public static string IconFile(int size) => $"icon-{size}.png";

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
        Sweep(checks);
        var run = Path.Combine(checks, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(run);
        var session = sessions.OpenCheck(
            app.Author, new AppOrigin(app.Id, Preview: true), request.CallToken, request.Household, started + limits.Deadline);
        var reachable = new HashSet<string>([address, .. settings.Cdns, .. request.Manifest.Connect], StringComparer.OrdinalIgnoreCase);
        using var deadline = new CancellationTokenSource(limits.Deadline, clock);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var viewports = new List<ViewportRun>();
        string? failure = null;
        try
        {
            using var playwright = await Playwright.CreateAsync().WaitAsync(stopped.Token);
            await using var browser = await CheckBrowser.OpenAsync(playwright, limits.Browser, CheckBrowser.Options(settings, address));
            await using var closing = stopped.Token.Register(() => _ = browser.CloseAsync());
            foreach (var viewport in request.Viewports)
            {
                var seen = new ViewportRun(viewport);
                viewports.Add(seen);
                await ViewAsync(browser, seen, address + request.Path, session, reachable, request.Assertions, run, stopped.Token);
            }

            await ShowIconAsync(browser, AppIcon.Svg(request.Manifest, IconDrawing(app, request.Manifest)), run, stopped.Token);
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
    // shown, read for structure, and asked its assertions — each only while the page is still the
    // preview. The route sees a navigation's first request alone, so an origin the app may reach can
    // redirect the browser anywhere; what was captured of anything but the preview is dropped, never kept.
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
        var preview = OriginOf(url);
        bool onPreview() => string.Equals(OriginOf(page.Url), preview, StringComparison.OrdinalIgnoreCase);
        page.Console += (_, message) =>
        {
            if (onPreview())
            {
                seen.Console.Enqueue(new ConsoleEntry(message.Type, message.Text, message.Location));
            }
        };
        page.PageError += (_, error) =>
        {
            if (onPreview())
            {
                seen.Console.Enqueue(new ConsoleEntry("uncaught", error, null));
            }
        };
        page.RequestFailed += (_, failed) => seen.Failed.Enqueue(new FailedRequest(failed.Method, failed.Url, null,
            failed.Failure is { } why && why.Contains("ERR_BLOCKED_BY_CLIENT", StringComparison.Ordinal)
                ? "blocked: a check's page reaches only its own origin, the CDNs and its manifest's connect origins"
                : failed.Failure));
        page.Response += (_, response) =>
        {
            if (response.Status == StatusCodes.Status422UnprocessableEntity && IsOn(response.Url, preview, $"{AppReads.OriginPath}/"))
            {
                seen.EnvelopeUrls.Enqueue(response.Url);
                seen.Refusals.Enqueue(RefusedAsync(response));
            }
            else if (response.Status >= 400 && IsOn(response.Url, preview, AppAsks.OriginPath))
            {
                seen.FailedAsks.Enqueue(FailedAskAsync(response, seen));
            }
            else if (response.Status >= 400)
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

        var shot = Path.Combine(run, viewport.Screenshot);
        if (onPreview())
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot, Timeout = 0 });
            var accessibility = await page.Locator("body").AriaSnapshotAsync(new LocatorAriaSnapshotOptions { Timeout = 0 });
            if (onPreview())
            {
                seen.Screenshot = viewport.Screenshot;
                seen.Accessibility = accessibility;
            }
            else
            {
                File.Delete(shot);
            }
        }

        foreach (var assertion in assertions.TakeWhile(_ => onPreview()))
        {
            var outcome = await AssertAsync(page, assertion, stopped);
            seen.Assertions.Add(onPreview() ? outcome : outcome with { Passed = false, Message = LeftThePreview });
        }

        if (!onPreview())
        {
            seen.Error = $"{LeftThePreview}; nothing it showed after that is kept";
        }

        await Task.WhenAll(seen.Refusals);
        await Task.WhenAll(seen.FailedAsks);
    }

    // The icon the preview would install with, drawn by the check's own browser from the server's own SVG
    // (the badge included) as a picture in a page of nothing else: what a launcher shows, at each size.
    private static async Task ShowIconAsync(IBrowser browser, string svg, string run, CancellationToken stopped)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = IconSizes.Max(), Height = IconSizes.Sum() }, DeviceScaleFactor = 1
        });
        var page = await context.NewPageAsync();
        var source = $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
        await page.SetContentAsync(
            "<body style=\"margin:0\">"
            + string.Concat(IconSizes.Select(size => $"<img id=\"icon-{size}\" src=\"{source}\" width=\"{size}\" height=\"{size}\" style=\"display:block\">"))
            + "</body>", new PageSetContentOptions { Timeout = 0 });
        await page.WaitForFunctionAsync("() => [...document.images].every(i => i.complete && i.naturalWidth > 0)", null,
            new PageWaitForFunctionOptions { Timeout = 0 }).WaitAsync(stopped);
        foreach (var size in IconSizes)
        {
            await page.Locator($"#icon-{size}").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(run, IconFile(size)), OmitBackground = true, Timeout = 0
            });
        }
    }

    // The drawing the working tree's manifest names, as the preview serves it: a file under its root, no link.
    private byte[]? IconDrawing(BuiltApp app, AppManifest manifest)
    {
        if (manifest.Icon is not { } icon)
        {
            return null;
        }

        var file = Path.Combine(store.Repository(app).WorkTree, manifest.Root, AppIcon.Relative(icon));
        return File.Exists(file) && new FileInfo(file).LinkTarget is null && new FileInfo(file).Length <= AppIcon.MaxBytes
            ? File.ReadAllBytes(file)
            : null;
    }

    // A read of the household (POST /_vfs/<operation>) or an ask (POST /_ask) the preview's library made, on
    // the preview itself.
    private static bool IsOn(string url, string preview, string path) =>
        string.Equals(OriginOf(url), preview, StringComparison.OrdinalIgnoreCase)
        && new Uri(url).AbsolutePath.StartsWith(path, StringComparison.Ordinal);

    // An ask answered with the tools' envelope is the page's to handle, as a refused read is: what it asked, why
    // it failed, and on the preview what fixes it (the reply that was no JSON, the agent's own words for a turn
    // it failed). Anything else answered there (no session's 404) stays a failed request.
    private static async Task<FailedAsk?> FailedAskAsync(IResponse response, ViewportRun seen)
    {
        var body = await BodyAsync(response);
        if (ToolErrorResult.FromEnvelope(body) is { } failed)
        {
            seen.EnvelopeUrls.Enqueue(response.Url);
            return new FailedAsk(response.Status, AskedText(response.Request.PostData), failed.ErrorCode, failed.Message,
                failed.Hint, TextAt(body, AppAsks.ReplyField), TextAt(body, AppAsks.FailureField));
        }

        seen.Failed.Enqueue(new FailedRequest(response.Request.Method, response.Url, response.Status, null));
        return null;
    }

    private static string? TextAt(JsonNode? body, string name) =>
        body?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Null for a body that is no JSON (the origin's 404 page) or one the page went away before it was read
    // (Playwright throws): either way there is no envelope, and the answer is a failed request.
    private static async Task<JsonNode?> BodyAsync(IResponse response)
    {
        try
        {
            return JsonNode.Parse(await response.TextAsync());
        }
        catch (Exception ex) when (ex is PlaywrightException or JsonException)
        {
            return null;
        }
    }

    private const int MaxAskedText = 200;

    // The ask's text as its wire names it (AppAskRequest, read with JSON's web names).
    private static readonly string _askedTextField = JsonNamingPolicy.CamelCase.ConvertName(nameof(AppAskRequest.Text));

    // What the page asked, when it asked in JSON (an ask with images is a form, its text left unsaid), clipped.
    private static string? AskedText(string? body)
    {
        try
        {
            using var read = JsonDocument.Parse(body ?? "");
            var text = Text(read.RootElement, _askedTextField);
            return text is { Length: > MaxAskedText } ? $"{text[..MaxAskedText]}…" : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // What a refused read asked (its operation, and the path its arguments name) and why it was refused,
    // off the tools' envelope; a page gone before the body was read leaves the why unsaid.
    private static async Task<RefusedRead> RefusedAsync(IResponse response)
    {
        var operation = new Uri(response.Url).AbsolutePath[(AppReads.OriginPath.Length + 1)..];
        var path = PathOf(response.Request.PostData);
        try
        {
            using var envelope = JsonDocument.Parse(await response.TextAsync());
            return new RefusedRead(operation, path, Text(envelope.RootElement, "errorCode"), Text(envelope.RootElement, "message"));
        }
        catch (Exception ex) when (ex is PlaywrightException or JsonException)
        {
            return new RefusedRead(operation, path, null, null);
        }
    }

    private static readonly string[] _pathArguments = ["filePath", "basePath", "path", "directoryPath"];

    private static string? PathOf(string? arguments)
    {
        try
        {
            using var read = JsonDocument.Parse(arguments ?? "");
            return _pathArguments.Select(name => Text(read.RootElement, name)).FirstOrDefault(path => path is not null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    // What an earlier check left half done (a crash, a server stopped mid-check). A household runs one
    // check at a time, so nothing else is writing here.
    private static void Sweep(string checks)
    {
        if (!Directory.Exists(checks))
        {
            return;
        }

        foreach (var stale in Directory.EnumerateDirectories(checks)
                     .Where(d => Path.GetFileName(d) is var name && (name.StartsWith("run-") || name.StartsWith("retired-"))))
        {
            Directory.Delete(stale, recursive: true);
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

    private const string LeftThePreview = "the page left the preview for an origin the check does not show";

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
        public ConcurrentQueue<Task<RefusedRead>> Refusals { get; } = new();
        public ConcurrentQueue<Task<FailedAsk?>> FailedAsks { get; } = new();

        // The addresses answered with the tools' envelope: a refused read's, a failed ask's.
        public ConcurrentQueue<string> EnvelopeUrls { get; } = new();
        public List<AssertionOutcome> Assertions { get; } = [];
        public string? Screenshot { get; set; }
        public string? Accessibility { get; set; }
        public bool Settled { get; set; }
        public string? Error { get; set; }

        // Chromium logs every answer of 400 or worse as an error, at the address asked ("<url>:0:0"); for a
        // refused read or a failed ask the page may well have handled, that line says nothing the entry does not.
        private bool IsRefusalsOwn(ConsoleEntry entry) =>
            entry is { Type: "error", Location: { } at }
            && EnvelopeUrls.Any(url => at.StartsWith(url + ":", StringComparison.Ordinal));

        public ViewportReport Report() => new()
        {
            Name = viewport.Name,
            Width = viewport.Width,
            Height = viewport.Height,
            Screenshot = Screenshot,
            Settled = Settled,
            Error = Error,
            Console = [.. Console.Where(entry => !IsRefusalsOwn(entry)).Take(MaxEntries)],
            FailedRequests = [.. Failed.Take(MaxEntries)],
            PolicyViolations = [.. Violations.Take(MaxEntries)],
            RefusedReads = [.. Refusals.Where(refusal => refusal.IsCompletedSuccessfully).Select(refusal => refusal.Result).Take(MaxEntries)],
            FailedAsks =
            [
                .. FailedAsks.Where(ask => ask.IsCompletedSuccessfully).Select(ask => ask.Result).OfType<FailedAsk>().Take(MaxEntries)
            ],
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
// files, and the token of the call that asked, which the page reads and asks with, and the household that
// call proved, which its asks' cap is read on.
public sealed record CheckRequest(
    IReadOnlyList<CheckViewport> Viewports, string Path, AppManifest Manifest, IReadOnlyList<CheckAssertion> Assertions,
    string CallToken, ICallHousehold Household);

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

    // Reads of the household refused with the tools' envelope: the page's to handle, so not a failure.
    public required IReadOnlyList<RefusedRead> RefusedReads { get; init; }

    // Asks answered with the tools' envelope: the page's to handle too, so not a failure either.
    public required IReadOnlyList<FailedAsk> FailedAsks { get; init; }
    public string? Accessibility { get; init; }
    public required IReadOnlyList<AssertionOutcome> Assertions { get; init; }
}

public sealed record ConsoleEntry(string Type, string Text, string? Location);

// A request that failed outright (Error) or was answered 400 or worse (Status), but for a refused read.
public sealed record FailedRequest(string Method, string Url, int? Status, string? Error);

public sealed record RefusedRead(string Operation, string? Path, string? ErrorCode, string? Message);

// An ask answered with the tools' envelope: its status, what it asked (Text, when it was JSON), the envelope's
// code, message and hint, and on the preview the reply that was no JSON (Reply) or the agent's own words for
// a turn it failed (Failure).
public sealed record FailedAsk(
    int Status, string? Text, string ErrorCode, string? Message, string? Hint, string? Reply, string? Failure);

public sealed record PolicyViolation(string Directive, string? BlockedUri, string? SourceFile, int? Line, string? Sample);

public sealed record AssertionOutcome(string File, bool Passed, string Message);