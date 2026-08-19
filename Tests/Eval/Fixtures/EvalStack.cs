using System.Net;
using System.Text.Json;
using Agent.Modules;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Tools.Memory;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Clients.Browser;
using Infrastructure.Clients.HomeAssistant;
using Infrastructure.Clients.Voice;
using Mcp.Hosting;
using McpServerHomeAssistant.Modules;
using McpServerHomeAssistant.Settings;
using McpServerScheduling.Modules;
using McpServerScheduling.Settings;
using McpServerTimers.Modules;
using McpServerTimers.Settings;
using McpServerVault.Modules;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using StackExchange.Redis;
using Tests.Eval.Harness;
using Tests.Integration.Fixtures;
using HomeAssistantSettings = McpServerHomeAssistant.Settings.McpSettings;
using VaultSettings = McpServerVault.Settings.McpSettings;
using WebSearchSettings = McpServerWebSearch.Settings.McpSettings;

namespace Tests.Eval.Fixtures;

// Everything one scenario runs against: the real MCP servers in process, the outermost externals
// faked, and the agent built by the real factory from the shipped definition in
// Agent/appsettings.json. Only the configured endpoint urls are rewritten — reconstructing the
// wiring here would test a composition no deployment uses, which is the failure ADR-0030 exists
// to prevent.
public sealed class EvalStack : IAsyncDisposable
{
    // The satellites a scenario's voice turn can come from and ring on. Two rooms, because a
    // one-room roster cannot tell "defaulted to the speaking room" from "picked the only option".
    public static readonly SatelliteDescriptor[] Roster =
    [
        new("kitchen-01", "kitchen"),
        new("office-01", "office")
    ];

    private readonly List<IHost> _servers = [];
    private ServiceProvider? _agentServices;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public FakeVoiceHub VoiceHub { get; } = new(Roster);

    // The home behind the Home Assistant server: what a scenario reads to ask whether the change
    // it wanted happened, and whether anything else moved.
    public FakeHomeAssistant Home { get; } = new();

    // The directory the vault server is pointed at, so a scenario can read the notes back after
    // the turn. Per run, and deleted with the stack: a note edited by the previous run would make
    // this one's assertions about what survived meaningless.
    public string VaultPath { get; private set; } = "";

    // The site every web scenario browses, and the search engine that points at it. Served by the
    // test host so the pages hold still: a scenario asserting what a page says cannot depend on
    // somebody else's site, and no scenario in this suite reaches the internet.
    public EvalWeb Web { get; private set; } = null!;

    // Music Assistant, faked at its own server the way ADR-0030 says. It exists for one action —
    // a podcast's episode list — which Home Assistant has no service for and which is the only way
    // to obtain the uri an episode plays by.
    public FakeMusicAssistantServer Music { get; private set; } = null!;

    // What the user is remembered as having said. Recall rides the turn as data; this is what the
    // forget tool searches and deletes from, and what a scenario reads afterwards.
    public EvalMemory Memory { get; private set; } = null!;

    // The workers, canned. A scenario about delegation is about the parent's decision, so what a
    // worker would have answered is a fixed string the scenario chooses rather than another run.
    public CannedSubAgents Workers { get; private set; } = null!;

    // One instant for the agent's decoration and for every server, so an expected fire time is an
    // exact string. It never advances: a clock that ticks would make a timer's remaining seconds a
    // range, and a scenario asserting a range asserts almost nothing.
    public FakeTimeProvider Clock { get; private set; } = null!;

    public IAgentFactory Factory { get; private set; } = null!;

    public static async Task<EvalStack> StartAsync(
        Scenario scenario, string redisConnectionString, IToolInvocationObserver observer)
    {
        // Started as far back as the oldest running timer needs, then wound forward to the turn's
        // own instant once everything is armed. A clock that only ever sat at the instant would
        // arm every timer with its full duration left.
        var stack = new EvalStack
        {
            Workers = new CannedSubAgents(scenario.WorkerAnswer),
            Memory = new EvalMemory(scenario.Remembered),
            Clock = new FakeTimeProvider(
                scenario.Instant - scenario.Armed.Select(a => a.RunningFor).DefaultIfEmpty().Max())
        };

        // Madrid, because the deployment's satellites are there and a scheduled time is only exact
        // once the zone is. The provider carries it so every server and the turn decoration agree.
        stack.Clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"));

        var shipped = ShippedSettings(redisConnectionString);

        // All three mounts, on every scenario, because what the timer contract discriminates is
        // *which* of them a request belongs in. A stack that hosted only the mount the answer
        // lands in would leave the model no wrong place to put it, and a discrimination with one
        // option is not one.
        stack.BuildAgentServices(shipped, observer, new Dictionary<string, string>
        {
            ["mcp-timers"] = await stack.StartTimersAsync(scenario),
            ["mcp-scheduling"] = await stack.StartSchedulingAsync(shipped, redisConnectionString),
            ["mcp-homeassistant"] = await stack.StartHomeAssistantAsync(),
            ["mcp-vault"] = await stack.StartVaultAsync(),
            ["mcp-websearch"] = await stack.StartWebSearchAsync()
        });

        return stack;
    }

    private async Task<string> StartTimersAsync(Scenario scenario)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureTimers(new TimerSettings());

        // After the server's own registration, so the last word is the pinned one. The hub is
        // replaced at the transport rather than at the adapter: the adapters, their token header
        // and their failure mapping are production code a scenario is entitled to exercise.
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => (TimeProvider)Clock));
        builder.Services.AddHttpClient(VoiceHubHttp.ClientName, client =>
            {
                client.BaseAddress = new Uri("http://voice-hub.eval/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => VoiceHub);

        var endpoint = await StartAsync(builder, port);
        await ArmAsync(_servers[^1].Services.GetRequiredService<TimerFileSystem>(), scenario);

        return endpoint;
    }

    // Through the server's own create path, with the same JSON a model would write. Writing into
    // the store directly would arm a timer the server itself could not have produced, and a
    // scenario about changing a running timer would then be reading a state that never occurs.
    private async Task ArmAsync(TimerFileSystem timers, Scenario scenario)
    {
        foreach (var seed in scenario.Armed.OrderByDescending(a => a.RunningFor))
        {
            Clock.SetUtcNow(scenario.Instant - seed.RunningFor);

            var body = JsonSerializer.Serialize(new
            {
                durationSeconds = seed.DurationSeconds,
                text = seed.Text,
                target = new { room = seed.Room }
            });

            var result = await timers.CreateAsync(
                $"/{seed.Id}/timer.json", body, overwrite: false, createDirectories: true,
                CancellationToken.None);

            if (result is FsResult<FsCreateResult>.Err error)
            {
                throw new InvalidOperationException(
                    $"Could not arm '{seed.Id}' for the scenario: {error}");
            }
        }

        Clock.SetUtcNow(scenario.Instant);
    }

    // The vault, seeded into a temp directory the server is pointed at. Nothing is faked here —
    // the backend a deployment runs is a directory on disk, so the eval gives it one.
    private async Task<string> StartVaultAsync()
    {
        VaultPath = EvalVault.Seed();

        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new VaultSettings
        {
            VaultPath = VaultPath,
            AllowedExtensions = [".md", ".txt", ".json", ".yaml", ".yml"]
        });

        return await StartAsync(builder, port);
    }

    // Home Assistant's own server, with the home behind it faked at the REST API. The catalog, the
    // action files and the argument parsing a scenario exercises are therefore the deployment's.
    private async Task<string> StartHomeAssistantAsync()
    {
        Music = await FakeMusicAssistantServer.StartAsync();

        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new HomeAssistantSettings
        {
            HomeAssistant = new HomeAssistantConfiguration
            {
                BaseUrl = "http://home-assistant.eval",
                Token = FakeHomeAssistant.Token
            },
            // Configured, so the server advertises the podcast-episode action — a deployment
            // without Music Assistant does not, and a scenario about episodes would then be
            // testing a toolset nobody runs.
            MusicAssistant = new MusicAssistantConfiguration
            {
                BaseUrl = Music.BaseUrl,
                Token = FakeMusicAssistantServer.ValidToken
            }
        });

        // The typed client keeps its name from the interface it is registered against, so this
        // reaches the same client the server resolves and replaces only its transport.
        builder.Services.AddHttpClient(nameof(IHomeAssistantClient))
            .ConfigurePrimaryHttpMessageHandler(() => Home);

        return await StartAsync(builder, port);
    }

    // The websearch server, with both of its externals replaced and nothing else: Brave answers
    // from a table, and the browser is a local Chromium rather than the remote hardened Firefox a
    // deployment connects to. Everything between them — the tools, the session manager, the
    // accessibility snapshot, the modal dismisser — is the deployment's own.
    private async Task<string> StartWebSearchAsync()
    {
        Web = await EvalWeb.StartAsync();

        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new WebSearchSettings
        {
            BraveSearch = new BraveSearchConfiguration
            {
                ApiKey = "eval",
                ApiUrl = EvalWeb.SearchApiUrl
            }
        });

        // The typed client keeps its name from the interface it is registered against.
        builder.Services.AddHttpClient(nameof(IWebSearchClient))
            .ConfigurePrimaryHttpMessageHandler(() => Web.SearchTransport);

        builder.Services.Replace(ServiceDescriptor.Singleton<IWebBrowser>(
            _ => new PlaywrightWebBrowser(browserFactory: LaunchAsync)));

        return await StartAsync(builder, port);
    }

    // One browser per stack, launched the way every web fixture launches one.
    private async Task<IBrowser> LaunchAsync()
    {
        _playwright ??= await Playwright.CreateAsync();
        _browser = await EvalWeb.LaunchBrowserAsync(_playwright);

        return _browser;
    }

    // The scheduling server, on the same Redis the agent uses. Its keys are fixed names rather
    // than a per-run prefix, so the slate is wiped on the way in: a schedule left behind by the
    // previous run would show up in this one's listing and count against its ceiling.
    private async Task<string> StartSchedulingAsync(
        AgentSettings shipped, string redisConnectionString)
    {
        await ClearSchedulesAsync(redisConnectionString);

        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureScheduling(new SchedulingSettings
        {
            RedisConnectionString = redisConnectionString,
            // Long enough that nothing dispatches during a turn. The clock is pinned anyway, so a
            // due schedule is one the scenario armed rather than one the turn created.
            DispatchIntervalSeconds = 3600,
            DefaultDeliverTo = ["signalr"]
        });
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => (TimeProvider)Clock));

        var endpoint = await StartAsync(builder, port);

        // What the channel registration does in production, on a stack that dials no channels: the
        // agent directories under /schedules are the shipped agents, so a scenario that schedules
        // work writes into the same directory a deployment would.
        _servers[^1].Services.GetRequiredService<IMutableAgentCatalog>().Replace(
            [.. shipped.Agents.Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description))]);

        return endpoint;
    }

    private static async Task ClearSchedulesAsync(string redisConnectionString)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = connection.GetDatabase();
        var ids = await database.SetMembersAsync("schedules");

        await Task.WhenAll(ids.Select(id => database.KeyDeleteAsync($"schedule:{id}")));
        await database.KeyDeleteAsync(["schedules", "schedules:due"]);
    }

    private async Task<string> StartAsync(WebApplicationBuilder builder, int port)
    {
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        _servers.Add(app);

        return $"http://localhost:{port}/mcp";
    }

    // The shipped configuration, with two edits and no third: the secrets come from user secrets
    // rather than from the environment the container would have had, and nothing dials a channel —
    // the boundary of an eval is the agent, and what a channel does with a reply afterwards is
    // covered by the channel and end-to-end suites.
    private static AgentSettings ShippedSettings(string redisConnectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ShippedSettingsPath(), optional: false)
            .AddUserSecrets<EvalStack>()
            .AddEnvironmentVariables()
            .Build();

        var shipped = configuration.Get<AgentSettings>()
                      ?? throw new InvalidOperationException("Agent/appsettings.json did not bind.");

        return shipped with
        {
            Redis = new RedisConfiguration { ConnectionString = redisConnectionString },
            ChannelEndpoints = []
        };
    }

    // The agent itself, built by the real factory. Only the configured endpoint urls are rewritten,
    // to the servers this stack hosts.
    private void BuildAgentServices(
        AgentSettings shipped, IToolInvocationObserver observer,
        IReadOnlyDictionary<string, string> hosted)
    {
        var settings = shipped with
        {
            Agents = [.. shipped.Agents.Select(a => a with { McpServerEndpoints = Rewrite(a, hosted) })]
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgent(settings);
        services.AddSubAgents(settings.SubAgents);
        services.AddSingleton(observer);
        services.AddSingleton<ISubAgentSpawner>(Workers);

        // The memory feature, the way the deployment enables it: both shipped assistants list
        // `memory` among their features, so an eval without it would run a prompt one section
        // short. What is replaced is the store's backend and the embedding server — the tool, its
        // description and the prompt section are the deployment's.
        services.AddSingleton<IMemoryStore>(Memory);
        services.AddSingleton<IEmbeddingService>(Memory);
        services.AddTransient<IDomainToolFeature, MemoryToolFeature>();

        _agentServices = services.BuildServiceProvider();
        Factory = _agentServices.GetRequiredService<IAgentFactory>();
    }

    // An endpoint with nothing hosting it is dropped rather than left pointing at a compose host
    // name: a configured endpoint that cannot be dialled fails the whole session (ADR-0027), so
    // the alternative is a scenario that cannot run at all. The agent under test therefore has a
    // smaller toolset than the shipped one until every family's servers are hosted, and which
    // sections it actually got is readable in the dump's assembled prompt.
    private static string[] Rewrite(AgentDefinition agent, IReadOnlyDictionary<string, string> hosted) =>
        [.. agent.McpServerEndpoints
            .Select(endpoint => hosted.FirstOrDefault(h => endpoint.Contains(h.Key)).Value)
            .Where(rewritten => rewritten is not null)];

    private static string ShippedSettingsPath() =>
        Path.Combine(RepositoryRoot.Path, "Agent", "appsettings.json");

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_servers.Select(async server =>
        {
            await server.StopAsync();
            server.Dispose();
        }));

        if (_agentServices is not null)
        {
            await _agentServices.DisposeAsync();
        }

        await Music.DisposeAsync();
        await Web.DisposeAsync();

        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();

        if (Directory.Exists(VaultPath))
        {
            Directory.Delete(VaultPath, recursive: true);
        }
    }
}