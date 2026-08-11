using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tests.E2E.Fixtures;

// The containers behind every WebChat E2E collection, owned process-wide rather than per fixture.
//
// The suite used to run every WebChat E2E class as one serial chain behind a single collection,
// which was the whole of its wall clock: ~150s during which nothing else in the run was left to do.
// Splitting those classes into collections that run at once is only worth it if they keep sharing
// one stack — six containers per collection would cost more in start-up than the serialisation
// ever cost in waiting. So the two expensive phases are memoized (one build, one start, whoever
// asks first) and the containers are refcounted, torn down when the last collection lets go.
//
// Concurrency across collections is safe because a test's server-side state is scoped to the user
// it picks, and NextUserIndex hands out a distinct one per test across every collection.
internal sealed class WebChatStack
{
    private static readonly Lock _gate = new();
    private static Task? _images;
    private static Task<WebChatStack>? _started;
    private static int _refCount;

    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _mcpVault;
    private IContainer? _mcpChannelSignalR;
    private IContainer? _agent;
    private IContainer? _webui;
    private IContainer? _caddy;
    private WebApplication? _whisper;

    private int _sliceIndex = -1;

    // A user identity is the unit of server-side isolation between tests, so two collections
    // running at once must never draw the same one: a topic touched by one of them sorts to the
    // top of the other's sidebar mid-assertion. Each collection reserves its own block instead of
    // sharing a counter, which makes disjointness structural rather than a matter of how the
    // interleaving happened to land. The blocks are sized so a collection reuses an identity no
    // more often than the single serial chain used to.
    public const int UsersPerCollection = 8;
    public const int CollectionSlices = 3;
    public const int UserCount = UsersPerCollection * CollectionSlices;

    public string WebChatUrl { get; private set; } = "";

    // What the stubbed whisper answers, per test. No model and no GPU are involved: the browser,
    // the channel server, the ticket and the upload are all real, and only the transcription is
    // decided here. Only the dictation collection touches these, which is why that collection is
    // the one that owns them — no other collection running beside it reads or writes them.
    public string Transcript { get; set; } = "hola desde el micrófono";

    public int TranscriptionStatus { get; set; } = 200;

    // The bytes the browser actually posted, so a test can pin the format whisper is fed rather
    // than trust that the encoder did what it says.
    public byte[]? LastAudio { get; private set; }

    // Short enough that a test can hold the microphone past it without a two-minute wait. The
    // browser learns it from the limits call, so this is also what proves the client carries no
    // cap of its own.
    public TimeSpan RecordingCap { get; } = TimeSpan.FromSeconds(4);

    // The first dropdown index of a caller's own block. Reserved once per collection, in whatever
    // order the collections start.
    public int ReserveUserBlock() =>
        (int)((uint)Interlocked.Increment(ref _sliceIndex) % CollectionSlices) * UsersPerCollection;

    // Both phases are memoized separately so each keeps the caller's own budget: an image build is
    // allowed twenty minutes and a container start five, and folding them together would have a
    // cold build spend the smaller one. Whoever asks first does the work; the rest await it.
    public static Task EnsureImagesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return _images ??= BuildImagesAsync(ct);
        }
    }

    public static async Task<WebChatStack> AcquireAsync(CancellationToken ct)
    {
        Task<WebChatStack> started;
        lock (_gate)
        {
            started = _started ??= StartAsync(ct);
            _refCount++;
        }

        return await started;
    }

    // The last collection to finish takes the containers down. Anything else would leave them for
    // the run's lifetime, or tear them out from under a collection still using them.
    public static async Task ReleaseAsync()
    {
        WebChatStack? stack = null;
        Task<WebChatStack>? started;
        lock (_gate)
        {
            if (--_refCount > 0)
            {
                return;
            }

            started = _started;
            _started = null;
            _images = null;
        }

        if (started is not null)
        {
            try
            {
                stack = await started;
            }
            catch
            {
                // A stack that never started has nothing to dispose; the failure already reached
                // whoever awaited the acquire.
            }
        }

        if (stack is not null)
        {
            await stack.StopAsync();
        }
    }

    private static async Task BuildImagesAsync(CancellationToken ct)
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

    private static async Task<WebChatStack> StartAsync(CancellationToken ct)
    {
        var stack = new WebChatStack();
        await stack.StartContainersAsync(ct);
        return stack;
    }

    private async Task StartContainersAsync(CancellationToken ct)
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

        var whisperPort = await StartWhisperStubAsync();

        _mcpChannelSignalR = new ContainerBuilder(signalRImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-channel-signalr")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            // Dictation reaches whisper through the shared transcription client, so pointing its
            // base URL at the test's own listener is the whole of the substitution.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment(
                "DICTATION__TRANSCRIPTION__BASEURL", $"http://host.docker.internal:{whisperPort}/v1")
            .WithEnvironment("DICTATION__MAXLENGTH", RecordingCap.ToString())
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
        var e2EAppSettings = Encoding.UTF8.GetBytes($$"""
            {
              "openRouter": { "apiUrl": "https://openrouter.ai/api/v1/", "apiKey": "{{apiKey}}" },
              "redis": { "connectionString": "redis:6379" },
              // Without these the model/effort menu never renders, so nothing in the suite ever
              // exercised the control the drawer's gesture bugs live next to.
              "patchableModels": [
                { "id": "~deepseek/deepseek-v4-flash-latest:nitro", "name": "DeepSeek V4 Flash" },
                { "id": "google/gemini-3.1-flash-lite", "name": "Gemini 3.1 Flash Lite" }
              ],
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

        var webui = new ContainerBuilder(webuiImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("webui")
            .WithPortBinding(8080, true);
        webui = Enumerable.Range(0, UserCount).Aggregate(webui, (builder, index) => builder
            .WithEnvironment($"USERS__{index}__ID", $"TestUser-{index}")
            .WithEnvironment(
                $"USERS__{index}__AVATARURL",
                $"https://api.dicebear.com/7.x/bottts/svg?seed=test{index}"));
        _webui = webui
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
            // Without this route a dictation POST lands on the static webui and comes back 405,
            // mirroring DockerCompose/caddy/Caddyfile.
            "    handle /api/dictation* {\n" +
            "        reverse_proxy mcp-channel-signalr:8080\n" +
            "    }\n" +
            "    handle /api/agents* {\n" +
            "        reverse_proxy agent:8080\n" +
            "    }\n" +
            "    handle {\n" +
            "        reverse_proxy webui:8080\n" +
            "    }\n" +
            "}\n";

        var caddyfileBytes = Encoding.UTF8.GetBytes(testCaddyfile);

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

    // whisper-server's own route, answered in process. The channel container reaches it through
    // the host gateway, so nothing about the browser's path to the transcript is faked.
    private async Task<int> StartWhisperStubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.ListenAnyIP(0));
        builder.Logging.ClearProviders();
        _whisper = builder.Build();

        _whisper.MapPost("/v1/audio/transcriptions", async context =>
        {
            // Kept, so a test can assert on the format the browser really encoded.
            var form = await context.Request.ReadFormAsync();
            if (form.Files.FirstOrDefault() is { } posted)
            {
                using var buffer = new MemoryStream();
                await using var content = posted.OpenReadStream();
                await content.CopyToAsync(buffer);
                LastAudio = buffer.ToArray();
            }
            if (TranscriptionStatus != 200)
            {
                context.Response.StatusCode = TranscriptionStatus;
                await context.Response.WriteAsync("the transcriber is having a bad day");
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { text = Transcript, language = "es" }));
        });

        await _whisper.StartAsync();
        var address = _whisper.Urls.First();
        return new Uri(address).Port;
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

    private async Task StopAsync()
    {
        if (_whisper is not null)
        {
            await _whisper.DisposeAsync();
        }

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