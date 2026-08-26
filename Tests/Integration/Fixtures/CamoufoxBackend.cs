using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients.Browser;

namespace Tests.Integration.Fixtures;

// A Camoufox behind the browser-integration collections, owned process-wide rather than per
// fixture, refcounted, and torn down when the last collection using it lets go.
//
// Those classes used to be one collection, which is what xUnit serialises: their spans added up to
// about a minute, and on a run where the browser tests were the slow half that sum was the run's
// tail. They run at once now, and the only reason that is affordable is that they keep sharing a
// backend — a browser server per collection would cost more in start-up than the serialisation ever
// cost in waiting.
//
// There are three of them rather than one. Two classes here time production code paths and assert
// on the milliseconds: the modal dismisser must clear a page with no modals in under 800ms, and the
// stability wait must return in under 550ms. A browser server handing out pages to three other
// collections at the same time does not answer in those budgets, and the run where it was asked to
// failed all three of those assertions — so the timing classes take a server nothing else is
// driving. The rest are split again by whether they clear the shared context's cookies: the ones
// that do stay on one server serialised together, and the ones that only drive their own GUID
// sessions take the third, because serialising them behind that chain was the suite's last twenty
// seconds. Which class belongs to which is held by PlaywrightCollectionLayoutTests.
internal sealed class CamoufoxBackend
{
    internal const string SharedPool = "shared";
    internal const string QuietPool = "quiet";
    internal const string IsolatedPool = "isolated";

    private const string CamoufoxImageName = "camoufox:latest";

    private static readonly Lock _gate = new();
    private static readonly Dictionary<string, (Task<CamoufoxBackend> Started, int Refs)> _pools = [];

    private IContainer? _container;
    private string? _initializationError;

    public PlaywrightWebBrowser Browser { get; private set; } = null!;

    public string? WsEndpoint { get; private set; }

    public static async Task<CamoufoxBackend> AcquireAsync(string pool)
    {
        Task<CamoufoxBackend> started;
        lock (_gate)
        {
            var held = _pools.GetValueOrDefault(pool);
            started = held.Started ?? StartAsync(pool);
            _pools[pool] = (started, held.Refs + 1);
        }

        return await started;
    }

    public static async Task ReleaseAsync(string pool)
    {
        Task<CamoufoxBackend> started;
        lock (_gate)
        {
            if (_pools.GetValueOrDefault(pool) is not { Started: { } held } entry)
            {
                return;
            }

            if (entry.Refs > 1)
            {
                _pools[pool] = (held, entry.Refs - 1);
                return;
            }

            _pools.Remove(pool);
            started = held;
        }

        try
        {
            var backend = await started;
            await backend.StopAsync();
        }
        catch
        {
            // One that never started has nothing to dispose; the failure already reached whoever
            // awaited the acquire.
        }
    }

    private static async Task<CamoufoxBackend> StartAsync(string pool)
    {
        var backend = new CamoufoxBackend();

        // Try local wsEndpoint first (faster if Camoufox is already running) — but never for the
        // quiet pool, whose whole point is a server nothing else is driving. A developer's local
        // Camoufox is exactly the server the shared pool would also be handed, and the timing
        // classes would then be measuring it under the other collections' load.
        var local = pool != QuietPool && await backend.TryInitializeLocalAsync();
        if (local || await backend.TryInitializeContainerAsync())
        {
            return backend;
        }

        throw new InvalidOperationException(
            $"Could not initialize Camoufox browser. {backend._initializationError}");
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
                containerBuilder = TestContainers.Container(CamoufoxImageName);
            }
            else
            {
                var solutionRoot = E2E.Fixtures.TestHelpers.FindSolutionRoot();
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

                containerBuilder = TestContainers.Container(image);
            }

            _container = containerBuilder
                .WithPortBinding(9377, true)
                // Docker's default /dev/shm is 64MB, which is where a browser puts its shared
                // graphics and IPC buffers — the documented cause of a browser dying under load in
                // a container, and this suite drives parallel sessions through one. Every browser
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

    private async Task StopAsync()
    {
        await Browser.DisposeAsync();
        if (_container != null)
        {
            await _container.DisposeAsync();
        }

        // Image is intentionally not disposed to preserve Docker layer cache across test runs
    }
}