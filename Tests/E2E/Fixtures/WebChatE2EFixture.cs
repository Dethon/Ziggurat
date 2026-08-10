using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Configuration;

namespace Tests.E2E.Fixtures;

public class WebChatE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _mcpVault;
    private IContainer? _mcpChannelSignalR;
    private IContainer? _agent;
    private IContainer? _webui;
    private IContainer? _caddy;

    public string WebChatUrl { get; private set; } = "";
    private int _userIndex;

    // Returns the next user dropdown index (0-9) so each test uses a unique user identity,
    // avoiding server-side state pollution (stream resume, pending approvals) between tests.
    public int NextUserIndex() => _userIndex++ % 10;

    protected override async Task BuildImagesAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(GetOpenRouterApiKey()))
        {
            return;
        }

        var solutionRoot = TestHelpers.FindSolutionRoot();

        // Every leaf image is FROM base-sdk:latest, so this must complete first.
        await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, ct);

        // Leaf images are mutually independent; EnsureImageAsync serialises per-tag
        // internally, so distinct tags building at once is safe.
        await Task.WhenAll(
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.McpVault, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.ChannelSignalR, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.Agent, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.WebUi, ct));
    }

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        _network = new NetworkBuilder()
            .WithName($"e2e-webchat-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        var mcpVaultImageName = E2EImages.McpVault.ImageName;
        var signalRImageName = E2EImages.ChannelSignalR.ImageName;
        var agentImageName = E2EImages.Agent.ImageName;
        var webuiImageName = E2EImages.WebUi.ImageName;

        _redis = new ContainerBuilder("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        _mcpVault = new ContainerBuilder(mcpVaultImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-vault")
            .WithEnvironment("VAULTPATH", "/vault")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8080))
            .Build();
        await _mcpVault.StartAsync(ct);

        _mcpChannelSignalR = new ContainerBuilder(signalRImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-channel-signalr")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "test-agent")
            .WithEnvironment("AGENTS__0__NAME", "Test Agent")
            .WithEnvironment("AGENTS__1__ID", "vision-agent")
            .WithEnvironment("AGENTS__1__NAME", "Vision Agent")
            // POST negotiate succeeds only after Kestrel has fully bound routes; TCP-only
            // checks return before the app is actually serving requests.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(8080)
                    .ForPath("/hubs/chat/negotiate")
                    .WithMethod(HttpMethod.Post)))
            .Build();
        await _mcpChannelSignalR.StartAsync(ct);

        // Inject a minimal appsettings.json so the agent only connects to E2E services.
        // Raise with E2E_AGENT_LOG_LEVEL=Debug when a failing run needs the agent's own account
        // of a turn rather than the browser's.
        var agentLogLevel = Environment.GetEnvironmentVariable("E2E_AGENT_LOG_LEVEL") ?? "Information";
        // Without this, the default appsettings.json baked into the image also registers
        // channels for telegram/servicebus and extra MCP endpoints that don't exist here.
        var e2EAppSettings = System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "openRouter": { "apiUrl": "https://openrouter.ai/api/v1/", "apiKey": "{{apiKey}}" },
              "redis": { "connectionString": "redis:6379" },
              "agents": [
                {
                  "id": "test-agent",
                  "name": "Test Agent",
                  "model": "~deepseek/deepseek-v4-flash-latest:nitro",
                  "mcpServerEndpoints": [ "http://mcp-vault:8080/mcp" ],
                  "whitelistPatterns": ["__none__"],
                  // The model sometimes answers a plain greeting with a tool call, which raises
                  // an approval prompt no test asked for and leaks it onto every later page.
                  // The approval tests demand a tool call explicitly, so they still get one.
                  "customInstructions": "Use tools only when the message explicitly asks for an action that needs one; otherwise reply with plain text."
                },
                {
                  // The attachment E2E needs a model that accepts image input: capability is
                  // discovered from the provider and the composer refuses the send otherwise.
                  // Its own agent, because the other tests are tuned against the default
                  // agent's model; no tool endpoints, so the reply is always text and never
                  // an approval prompt.
                  "id": "vision-agent",
                  "name": "Vision Agent",
                  "model": "google/gemini-3.1-flash-lite",
                  "mcpServerEndpoints": [],
                  "whitelistPatterns": ["__none__"]
                }
              ],
              "channelEndpoints": [
                { "channelId": "Web", "endpoint": "http://mcp-channel-signalr:8080/mcp" }
              ],
              "Logging": { "LogLevel": { "Default": "{{agentLogLevel}}" } }
            }
            """);

        _agent = new ContainerBuilder(agentImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("agent")
            .WithCommand("--chat", "Web", "--reasoning")
            .WithResourceMapping(e2EAppSettings, "/app/appsettings.json")
            // Wait until Kestrel + hosted services (ChatMonitoring) are running. The agent
            // container has no published host port, so an HTTP probe isn't possible; the
            // log line is the cheapest reliable signal.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Application started"))
            .Build();
        await _agent.StartAsync(ct);

        _webui = new ContainerBuilder(webuiImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("webui")
            .WithPortBinding(8080, true)
            .WithEnvironment("USERS__0__ID", "TestUser-0")
            .WithEnvironment("USERS__0__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test0")
            .WithEnvironment("USERS__1__ID", "TestUser-1")
            .WithEnvironment("USERS__1__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test1")
            .WithEnvironment("USERS__2__ID", "TestUser-2")
            .WithEnvironment("USERS__2__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test2")
            .WithEnvironment("USERS__3__ID", "TestUser-3")
            .WithEnvironment("USERS__3__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test3")
            .WithEnvironment("USERS__4__ID", "TestUser-4")
            .WithEnvironment("USERS__4__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test4")
            .WithEnvironment("USERS__5__ID", "TestUser-5")
            .WithEnvironment("USERS__5__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test5")
            .WithEnvironment("USERS__6__ID", "TestUser-6")
            .WithEnvironment("USERS__6__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test6")
            .WithEnvironment("USERS__7__ID", "TestUser-7")
            .WithEnvironment("USERS__7__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test7")
            .WithEnvironment("USERS__8__ID", "TestUser-8")
            .WithEnvironment("USERS__8__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test8")
            .WithEnvironment("USERS__9__ID", "TestUser-9")
            .WithEnvironment("USERS__9__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test9")
            // Empty string: browser falls back to relative /hubs/chat, which Caddy routes to mcp-channel-signalr
            .WithEnvironment("AGENTURL", "")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/manifest.webmanifest")))
            .Build();
        await _webui.StartAsync(ct);

        var testCaddyfile =
            ":80 {\n" +
            "    handle /hubs/* {\n" +
            "        reverse_proxy mcp-channel-signalr:8080\n" +
            "    }\n" +
            // The upload store lives beside the hub; without this route an upload POST lands on
            // the static webui and comes back 405, mirroring DockerCompose/caddy/Caddyfile.
            "    handle /api/attachments* {\n" +
            "        reverse_proxy mcp-channel-signalr:8080\n" +
            "    }\n" +
            "    handle /api/agents* {\n" +
            "        reverse_proxy agent:8080\n" +
            "    }\n" +
            "    handle {\n" +
            "        reverse_proxy webui:8080\n" +
            "    }\n" +
            "}\n";

        var caddyfileBytes = System.Text.Encoding.UTF8.GetBytes(testCaddyfile);

        _caddy = new ContainerBuilder("caddy:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("caddy")
            .WithPortBinding(80, true)
            .WithResourceMapping(caddyfileBytes, "/etc/caddy/Caddyfile")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/manifest.webmanifest")))
            .Build();
        await _caddy.StartAsync(ct);

        var host = _caddy.Hostname;
        var port = _caddy.GetMappedPublicPort(80);
        var baseUrl = $"http://{host}:{port}";

        // Wait for the full stack to be reachable through Caddy:
        // SignalR negotiate endpoint is reachable (proves Caddy → mcp-channel-signalr routing works)
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow.Add(TimeSpan.FromMinutes(2));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.PostAsync($"{baseUrl}/hubs/chat/negotiate?negotiateVersion=1", null, ct);
                if ((int)response.StatusCode < 500)
                {
                    break;
                }
            }
            catch
            {
                // ignore connection errors during startup
            }
            await Task.Delay(1_000, ct);
        }

        WebChatUrl = $"{baseUrl}/";
    }

    private static string? GetOpenRouterApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER__APIKEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            return envKey;
        }

        try
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets("bae64127-c00e-4499-8325-0fb6b452133c")
                .Build();
            return config["OpenRouter:ApiKey"];
        }
        catch
        {
            return null;
        }
    }

    protected override async Task StopContainersAsync()
    {
        if (_caddy is not null)
        {
            await _caddy.DisposeAsync();
        }

        if (_webui is not null)
        {
            await _webui.DisposeAsync();
        }

        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }

        if (_mcpChannelSignalR is not null)
        {
            await _mcpChannelSignalR.DisposeAsync();
        }

        if (_mcpVault is not null)
        {
            await _mcpVault.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}

[CollectionDefinition("WebChatE2E")]
public class WebChatE2ECollection : ICollectionFixture<WebChatE2EFixture>;