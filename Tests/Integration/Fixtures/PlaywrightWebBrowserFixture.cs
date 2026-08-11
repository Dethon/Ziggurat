using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Infrastructure.Clients.Browser;

namespace Tests.Integration.Fixtures;

public class PlaywrightWebBrowserFixture : IAsyncLifetime
{
    private const string CamoufoxImageName = "camoufox:latest";

    private IContainer? _container;
    private string? _initializationError;

    public PlaywrightWebBrowser Browser { get; private set; } = null!;
    public bool IsAvailable => true;
    public string? InitializationError => null;

    // The Camoufox WebSocket the Browser is connected to. Exposed so tests that need a raw
    // Playwright connection (e.g. verifying the server survives a hostile page) can reach the
    // same backend without standing up their own container.
    public string? WsEndpoint { get; private set; }

    public async Task InitializeAsync()
    {
        // Try local wsEndpoint first (faster if Camoufox is already running)
        if (await TryInitializeLocalAsync())
        {
            return;
        }

        if (await TryInitializeContainerAsync())
        {
            return;
        }

        throw new InvalidOperationException(
            $"Could not initialize Camoufox browser. {_initializationError}");
    }

    private async Task<bool> TryInitializeLocalAsync()
    {
        var localWsEndpoint = Environment.GetEnvironmentVariable("CAMOUFOX__WSENDPOINT");
        if (string.IsNullOrEmpty(localWsEndpoint))
        {
            return false;
        }

        try
        {
            Browser = new PlaywrightWebBrowser(wsEndpoint: localWsEndpoint);
            WsEndpoint = localWsEndpoint;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var request = new BrowseRequest(
                SessionId: "test-init",
                Url: "https://example.com",
                MaxLength: 1000);
            var result = await Browser.NavigateAsync(request, cts.Token);

            if (result.Status == BrowseStatus.Success)
            {
                await Browser.CloseSessionAsync("test-init", cts.Token);
                return true;
            }

            await Browser.DisposeAsync();
            _initializationError = result.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            _initializationError = $"Local Camoufox failed: {ex.Message}";
            await Browser.DisposeAsync();
            return false;
        }
    }

    private static string FindSolutionRoot() => E2E.Fixtures.TestHelpers.FindSolutionRoot();

    private static async Task<bool> CamoufoxImageExistsAsync()
    {
        try
        {
            using var client = new DockerClientBuilder().Build();
            var images = await client.Images.ListImagesAsync(new ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["reference"] = new Dictionary<string, bool> { [CamoufoxImageName] = true }
                }
            });

            return images.Count > 0;
        }
        catch
        {
            // If the image cannot be queried, fall back to building it.
            return false;
        }
    }

    private async Task<bool> TryInitializeContainerAsync()
    {
        try
        {
            ContainerBuilder containerBuilder;
            if (await CamoufoxImageExistsAsync())
            {
                // Reuse the existing image. Rebuilding via ImageFromDockerfileBuilder
                // would inject a fresh org.testcontainers.session-id label, producing
                // a new image id every run and orphaning the prior 7GB image as a
                // dangling layer even though the build is a full cache hit.
                containerBuilder = new ContainerBuilder(CamoufoxImageName);
            }
            else
            {
                var solutionRoot = FindSolutionRoot();
                var dockerfileDir = Path.Combine(solutionRoot, "DockerCompose", "camoufox");

                var image = new ImageFromDockerfileBuilder()
                    .WithDockerfileDirectory(dockerfileDir)
                    .WithDockerfile("Dockerfile")
                    .WithName(CamoufoxImageName)
                    .WithDeleteIfExists(false)
                    .WithCleanUp(false)
                    .Build();

                using var buildCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await image.CreateAsync(buildCts.Token);

                containerBuilder = new ContainerBuilder(image);
            }

            _container = containerBuilder
                .WithPortBinding(9377, true)
                // Docker's default /dev/shm is 64MB, which is where a browser puts its shared
                // graphics and IPC buffers — the documented cause of a browser dying under load in
                // a container, and this suite drives parallel sessions through one. A whole
                // collection rides on this container: when the server went down mid-run, nineteen
                // tests failed together on a closed WebSocket, none of them for a reason of their
                // own. The compose service survives the same crash because it is set to restart;
                // nothing restarts this one.
                .WithCreateParameterModifier(p =>
                    (p.HostConfig ??= new()).ShmSize = 1024L * 1024 * 1024)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(9377).ForPath("/json")))
                .Build();

            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _container.StartAsync(startCts.Token);

            var host = _container.Hostname;
            var port = _container.GetMappedPublicPort(9377);

            WsEndpoint = $"ws://{host}:{port}/browser";
            Browser = new PlaywrightWebBrowser(wsEndpoint: WsEndpoint);

            using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var request = new BrowseRequest(
                SessionId: "test-init",
                Url: "https://example.com",
                MaxLength: 1000);
            var result = await Browser.NavigateAsync(request, warmupCts.Token);

            if (result.Status == BrowseStatus.Success)
            {
                await Browser.CloseSessionAsync("test-init", warmupCts.Token);
                return true;
            }

            _initializationError = $"Container browser failed: {result.ErrorMessage}";
            return false;
        }
        catch (Exception ex)
        {
            _initializationError = $"Container initialization failed: {ex.Message}";
            return false;
        }
    }

    public Task ClearContextStateAsync() => Browser.ClearCookiesAsync();

    // JS-heavy live pages render form elements client-side after
    // navigation, so asserting on a single snapshot taken right after NavigateAsync is a race.
    // Waits for the actual condition instead of guessing at a fixed delay.
    public async Task<string> WaitForSnapshotAsync(
        string sessionId,
        Func<string, bool> predicate,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var start = Stopwatch.GetTimestamp();

        string? lastError = null;
        while (true)
        {
            var result = await Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            lastError = result.ErrorMessage;
            if (result is { ErrorMessage: null, Snapshot: not null } && predicate(result.Snapshot))
            {
                return result.Snapshot;
            }

            if (Stopwatch.GetElapsedTime(start) >= effectiveTimeout)
            {
                throw new TimeoutException(
                    $"Timed out after {effectiveTimeout.TotalSeconds:0}s waiting for {description}. " +
                    $"Last snapshot error: {lastError ?? "none"}.");
            }

            await Task.Delay(interval);
        }
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }

        // Image is intentionally not disposed to preserve Docker layer cache across test runs
    }
}

[CollectionDefinition("PlaywrightWebBrowserIntegration")]
public class PlaywrightWebBrowserIntegrationCollection : ICollectionFixture<PlaywrightWebBrowserFixture>;