using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Tests.E2E.Fixtures;

namespace Tests.Integration.Fixtures;

// Brings up the five MCP servers jonas connects to (mcp-vault, mcp-sandbox, mcp-websearch,
// mcp-idealista, mcp-homeassistant) and the SignalR channel server as real Docker containers,
// so the benchmark times the actual MCP handshake + tool/resource discovery surface area.
// mcp-homeassistant is backed by a real Home Assistant container (see HomeAssistantSeed) on the
// same network, so the home_assistant_guide prompt's live catalog fetch exercises the real path
// instead of a stub — the benchmark measures genuine session-build overhead.
// Images are rebuilt only when source under the watched directories has changed
// (see TestHelpers.EnsureImageAsync).
public class JonasMcpStackFixture : IAsyncLifetime
{
    private static readonly TimeSpan _startupTimeout = TimeSpan.FromMinutes(15);

    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _homeAssistant;
    private string? _haConfigDir;
    private IContainer? _mcpVault;
    private IContainer? _mcpSandbox;
    private IContainer? _mcpWebsearch;
    private IContainer? _mcpIdealista;
    private IContainer? _mcpHomeassistant;
    private IContainer? _mcpChannelSignalR;

    public IReadOnlyList<string> Endpoints { get; private set; } = [];

    // SignalR channel endpoints — the agent connects to /mcp as an MCP client, while a
    // browser-style client connects to /hubs/chat.
    public string SignalRChannelMcpEndpoint { get; private set; } = null!;
    public string SignalRHubUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var cts = new CancellationTokenSource(_startupTimeout);
        var ct = cts.Token;

        var solutionRoot = TestHelpers.FindSolutionRoot();

        // Build (or reuse) each image. EnsureImageAsync skips the rebuild when source under
        // the watched dirs hasn't changed since the existing image was created.
        // base-sdk must come first: every leaf Dockerfile does FROM base-sdk:latest and
        // publishes with BuildProjectReferences=false, so Domain/Infrastructure code is
        // frozen into base-sdk. Without this, leaf images silently carry stale Domain/
        // Infrastructure whenever those change.
        await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, ct);

        // Leaf images are mutually independent (each is FROM base-sdk:latest with
        // BuildProjectReferences=false), so build them concurrently once base-sdk exists.
        // EnsureImageAsync already serialises per-tag via its own semaphore + file lock,
        // so distinct tags building in parallel is safe by construction.
        await Task.WhenAll(
            TestHelpers.EnsureImageAsync(
                solutionRoot, "McpServerVault/Dockerfile", "mcp-vault:latest",
                ["Domain", "Infrastructure", "McpServerVault"], ct),
            TestHelpers.EnsureImageAsync(
                solutionRoot, "McpServerSandbox/Dockerfile", "mcp-sandbox:latest",
                ["Domain", "Infrastructure", "McpServerSandbox"], ct),
            TestHelpers.EnsureImageAsync(
                solutionRoot, "McpServerWebSearch/Dockerfile", "mcp-websearch:latest",
                ["Domain", "Infrastructure", "McpServerWebSearch"], ct),
            TestHelpers.EnsureImageAsync(
                solutionRoot, "McpServerIdealista/Dockerfile", "mcp-idealista:latest",
                ["Domain", "Infrastructure", "McpServerIdealista"], ct),
            TestHelpers.EnsureImageAsync(
                solutionRoot, "McpServerHomeAssistant/Dockerfile", "mcp-homeassistant:latest",
                ["Domain", "Infrastructure", "McpServerHomeAssistant"], ct),
            TestHelpers.EnsureImageAsync(
                solutionRoot, "McpChannelSignalR/Dockerfile", "mcp-channel-signalr:latest",
                ["Domain", "Infrastructure", "McpChannelSignalR"], ct));

        _network = TestContainers.Network()
            .WithName($"benchmark-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // The channel server expects Redis at "redis:6379" on this network for state.
        _redis = TestContainers.Container("redis/redis-stack:latest")
            .WithName($"redis-bench-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        // Boot a real Home Assistant on the benchmark network under the alias the agent's HA client
        // expects by default (http://homeassistant:8123). Started before the MCP servers so its
        // ~30-60s cold start overlaps their startup; the API-ready wait runs once everything is up.
        _haConfigDir = Path.Combine(Path.GetTempPath(), $"ha-bench-{Guid.NewGuid():N}");
        var haToken = HomeAssistantSeed.WriteConfig(_haConfigDir);
        _homeAssistant = TestContainers.Container(HomeAssistantSeed.ContainerImage)
            .WithName($"homeassistant-bench-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("homeassistant")
            .WithBindMount(_haConfigDir, "/config")
            .WithEnvironment("TZ", "UTC")
            .WithPortBinding(HomeAssistantSeed.Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(HomeAssistantSeed.Port))
            .Build();
        await _homeAssistant.StartAsync(ct);

        _mcpVault = await StartContainer("mcp-vault:latest", "mcp-vault", ct);
        _mcpSandbox = await StartContainer("mcp-sandbox:latest", "mcp-sandbox", ct);
        _mcpWebsearch = await StartContainer("mcp-websearch:latest", "mcp-websearch", ct);
        _mcpIdealista = await StartContainer("mcp-idealista:latest", "mcp-idealista", ct);
        // mcp-homeassistant reaches the real HA above via the default BaseUrl (network alias
        // `homeassistant`); it only needs the matching long-lived token.
        _mcpHomeassistant = await StartContainer(
            "mcp-homeassistant:latest", "mcp-homeassistant", ct,
            new Dictionary<string, string> { ["HOMEASSISTANT__TOKEN"] = haToken });

        _mcpChannelSignalR = TestContainers.Container("mcp-channel-signalr:latest")
            .WithName($"mcp-channel-signalr-bench-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-channel-signalr")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "jonas")
            .WithEnvironment("AGENTS__0__NAME", "Jonas")
            .WithEnvironment("AGENTS__0__DESCRIPTION", "General assistant")
            // POST against the SignalR negotiate endpoint succeeds (200) only after Kestrel
            // has fully started and the DI graph (including Redis connect) is built. TCP-only
            // checks return before the channel can actually serve MCP requests, which leaves
            // the agent's McpClient.CreateAsync racing the app startup.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(8080)
                    .ForPath("/hubs/chat/negotiate")
                    .WithMethod(HttpMethod.Post)))
            .Build();
        await _mcpChannelSignalR.StartAsync(ct);

        // All containers are up; ensure HA's REST API is fully ready (states/services/template) before
        // tests run, so the first session build hits a live HA rather than one still booting.
        var haBaseUrl = $"http://{_homeAssistant.Hostname}:{_homeAssistant.GetMappedPublicPort(HomeAssistantSeed.Port)}";
        await HomeAssistantSeed.WaitForApiReadyAsync(_homeAssistant, haBaseUrl, haToken);

        Endpoints =
        [
            EndpointFor(_mcpVault),
            EndpointFor(_mcpSandbox),
            EndpointFor(_mcpWebsearch),
            EndpointFor(_mcpIdealista),
            EndpointFor(_mcpHomeassistant),
        ];

        SignalRChannelMcpEndpoint = $"http://{_mcpChannelSignalR.Hostname}:{_mcpChannelSignalR.GetMappedPublicPort(8080)}/mcp";
        SignalRHubUrl = $"http://{_mcpChannelSignalR.Hostname}:{_mcpChannelSignalR.GetMappedPublicPort(8080)}/hubs/chat";
    }

    private async Task<IContainer> StartContainer(
        string image, string alias, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var builder = TestContainers.Container(image)
            .WithName($"{alias}-bench-{Guid.NewGuid():N}")
            .WithNetwork(_network!)
            .WithNetworkAliases(alias)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8080));

        builder = (environment ?? new Dictionary<string, string>())
            .Aggregate(builder, (b, kv) => b.WithEnvironment(kv.Key, kv.Value));

        var container = builder.Build();
        await container.StartAsync(ct);
        return container;
    }

    private static string EndpointFor(IContainer container)
        => $"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}/mcp";

    public async Task DisposeAsync()
    {
        var containers = new[]
        {
            _mcpChannelSignalR,
            _mcpHomeassistant, _mcpIdealista, _mcpWebsearch, _mcpSandbox, _mcpVault,
            _homeAssistant, _redis,
        };
        foreach (var container in containers)
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
        if (_haConfigDir is not null && Directory.Exists(_haConfigDir))
        {
            try
            { Directory.Delete(_haConfigDir, recursive: true); }
            catch { /* best effort — container may still hold handles momentarily */ }
        }
    }
}