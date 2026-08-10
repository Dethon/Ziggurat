using Microsoft.Playwright;

namespace Tests.E2E.Fixtures;

public abstract class E2EFixtureBase : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    protected virtual TimeSpan ContainerStartupTimeout => TimeSpan.FromMinutes(5);
    protected virtual TimeSpan ImageBuildTimeout => TimeSpan.FromMinutes(20);

    public async Task InitializeAsync()
    {
        var headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false";

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            // A dictation needs a microphone. The fake device answers getUserMedia with a
            // generated tone and the fake UI grants the permission prompt, so a recording is real
            // input through the real pipeline with no hardware and nobody to click "allow".
            Args =
            [
                "--use-fake-device-for-media-stream",
                "--use-fake-ui-for-media-stream",
                "--autoplay-policy=no-user-gesture-required"
            ]
        });

        // The fixtures initialise concurrently and share per-tag image locks, so one of them
        // spends most of this phase queued behind the other's base-sdk build having started no
        // work of its own. Budgeting builds separately stops that wait from consuming the budget
        // that exists for starting containers — which is what made the smaller-budget fixture
        // give up first on a cold run while the other went on to pass.
        var fixtureName = GetType().Name;
        await E2EPhase.RunAsync(fixtureName, "image build", ImageBuildTimeout, BuildImagesAsync);
        await E2EPhase.RunAsync(fixtureName, "container startup", ContainerStartupTimeout, StartContainersAsync);
    }

    // isMobile is not a synonym for hasTouch: it turns on Chromium's mobile emulation — the
    // meta-viewport, and with it the tap heuristics that decide whether a touch becomes a click.
    // A gesture test that only sets hasTouch is still being judged by desktop rules, which is
    // how a tap bug can reproduce on a phone and pass here.
    public async Task<IPage> CreatePageAsync(bool hasTouch = false, bool isMobile = false)
    {
        if (_browser is null)
        {
            throw new InvalidOperationException("Browser not initialized. Call InitializeAsync first.");
        }

        foreach (var ctx in _browser.Contexts.ToList())
        {
            await SaveTraceAsync(ctx);
            await ctx.CloseAsync();
        }

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            HasTouch = hasTouch || isMobile,
            IsMobile = isMobile,
            DeviceScaleFactor = isMobile ? 3 : null,
            ViewportSize = isMobile ? new ViewportSize { Width = 390, Height = 844 } : null
        });
        // The fake UI already answers the prompt; granting as well covers the permissions API,
        // which the page may consult before ever calling getUserMedia.
        await context.GrantPermissionsAsync(["microphone"]);
        await StartTraceAsync(context);

        // The app references third-party CDNs (Google Fonts, avatar service) as render-blocking
        // resources. When those are unreachable — offline, or a restrictive VPN like Cloudflare
        // WARP that black-holes WSL→internet — the page's load event never fires and navigation
        // times out. Tests only need the locally served app, so abort anything off the test host.
        await context.RouteAsync("**/*", async route =>
        {
            var url = route.Request.Url;
            var isExternal = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !url.Contains("localhost", StringComparison.OrdinalIgnoreCase);
            if (isExternal)
            {
                await route.AbortAsync();
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        return await context.NewPageAsync();
    }

    // Opt-in with PLAYWRIGHT_TRACE=1. A trace zip per page, saved when the next page replaces it
    // or the fixture shuts down, so a run that fails deep inside the suite still leaves the
    // browser-side timeline behind. Off by default: tracing costs time on every test.
    private static string? TraceDirectory =>
        Environment.GetEnvironmentVariable("PLAYWRIGHT_TRACE") == "1"
            ? Environment.GetEnvironmentVariable("PLAYWRIGHT_TRACE_DIR")
              ?? Path.Combine(Path.GetTempPath(), "playwright-traces")
            : null;

    private int _traceIndex;

    private async Task StartTraceAsync(IBrowserContext context)
    {
        if (TraceDirectory is null)
        {
            return;
        }

        await context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = false,
            Title = $"{GetType().Name}-{Interlocked.Increment(ref _traceIndex)}"
        });
    }

    private async Task SaveTraceAsync(IBrowserContext context)
    {
        if (TraceDirectory is not { } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{GetType().Name}-{_traceIndex}.zip");
        await context.Tracing.StopAsync(new TracingStopOptions { Path = path });
    }

    protected abstract Task BuildImagesAsync(CancellationToken ct);
    protected abstract Task StartContainersAsync(CancellationToken ct);
    protected abstract Task StopContainersAsync();

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            foreach (var ctx in _browser.Contexts.ToList())
            {
                await SaveTraceAsync(ctx);
            }
        }

        await StopContainersAsync();

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}