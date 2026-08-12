using Microsoft.Playwright;

namespace Tests.E2E.Fixtures;

public abstract class E2EFixtureBase : IAsyncLifetime
{
    // One Chromium for the whole run, handed out by reference count, and a fixture keeps only the
    // contexts it made. A browser per fixture was what capped how far the E2E suite could be split:
    // six collections meant six browsers and Chromium started dying under the memory, which is a
    // whole collection's worth of tests failing together on a closed target. Contexts are already
    // the isolation boundary — a test gets a fresh one either way — so the second browser was only
    // ever paying for itself in memory.
    private static readonly SemaphoreSlim _browserGate = new(1, 1);
    private static IPlaywright? _sharedPlaywright;
    private static IBrowser? _sharedBrowser;
    private static int _browserRefs;

    private IBrowser? _browser;
    private readonly List<IBrowserContext> _mine = [];

    protected virtual TimeSpan ContainerStartupTimeout => TimeSpan.FromMinutes(5);
    protected virtual TimeSpan ImageBuildTimeout => TimeSpan.FromMinutes(20);

    public async Task InitializeAsync()
    {
        // Launched alongside the containers rather than before them. The two have nothing to say to
        // each other until a test asks for a page, and the stack is the run's long pole — every
        // second Chromium spent starting was a second the first container had not begun. The
        // fixture that boots the stack for everybody was the one paying it.
        var fixtureName = GetType().Name;
        var containers = Task.Run(async () =>
        {
            // The fixtures initialise concurrently and share per-tag image locks, so one of them
            // spends most of this phase queued behind the other's base-sdk build having started no
            // work of its own. Budgeting builds separately stops that wait from consuming the
            // budget that exists for starting containers — which is what made the smaller-budget
            // fixture give up first on a cold run while the other went on to pass.
            await E2EPhase.RunAsync(fixtureName, "image build", ImageBuildTimeout, BuildImagesAsync);
            await E2EPhase.RunAsync(fixtureName, "container startup", ContainerStartupTimeout, StartContainersAsync);
        });

        await Task.WhenAll(LaunchBrowserAsync(), containers);
    }

    private async Task LaunchBrowserAsync()
    {
        await _browserGate.WaitAsync();
        try
        {
            _sharedBrowser ??= await StartSharedBrowserAsync();
            _browserRefs++;
            _browser = _sharedBrowser;
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private static async Task<IBrowser> StartSharedBrowserAsync()
    {
        var headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false";

        _sharedPlaywright = await Playwright.CreateAsync();
        return await _sharedPlaywright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless,
            // A dictation needs a microphone. The fake device answers getUserMedia and the fake UI
            // grants the permission prompt, so a recording is real input through the real pipeline
            // with no hardware and nobody to click "allow". It is fed a known two-tone file rather
            // than Chromium's default beep, so a test can ask what came out the far end and not
            // merely how many bytes did.
            Args =
            [
                "--use-fake-device-for-media-stream",
                $"--use-file-for-fake-audio-capture={FakeMicrophoneAudio.WriteToTempFile()}",
                "--use-fake-ui-for-media-stream",
                "--autoplay-policy=no-user-gesture-required"
            ]
        });
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

        // This fixture's own contexts only. The browser is shared now, so closing everything on it
        // would shut a page a collection running beside this one is still driving.
        foreach (var ctx in _mine.ToList())
        {
            await SaveTraceAsync(ctx);
            await ctx.CloseAsync();
            _mine.Remove(ctx);
        }

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            HasTouch = hasTouch || isMobile,
            IsMobile = isMobile,
            DeviceScaleFactor = isMobile ? 3 : null,
            ViewportSize = isMobile ? new ViewportSize { Width = 390, Height = 844 } : null
        });
        _mine.Add(context);
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
        foreach (var ctx in _mine.ToList())
        {
            await SaveTraceAsync(ctx);
            await ctx.CloseAsync();
        }

        _mine.Clear();
        await StopContainersAsync();

        // The last fixture out turns the browser off. Anything else would close it under a
        // collection still driving pages of its own.
        await _browserGate.WaitAsync();
        try
        {
            if (_browser is null || --_browserRefs > 0)
            {
                return;
            }

            if (_sharedBrowser is not null)
            {
                await _sharedBrowser.DisposeAsync();
                _sharedBrowser = null;
            }

            _sharedPlaywright?.Dispose();
            _sharedPlaywright = null;
        }
        finally
        {
            _browserGate.Release();
        }
    }
}