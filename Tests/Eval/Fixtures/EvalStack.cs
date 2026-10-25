using System.Net;
using System.Text.Json;
using Agent.App;
using Agent.Modules;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Tools.FileSystem.Bridge;
using Domain.Tools.Memory;
using Domain.Tools.Timers.Vfs;
using Domain.Tools.Web;
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
using McpServerSugary.Modules;
using McpServerTimers.Modules;
using McpServerTimers.Settings;
using McpServerVault.Modules;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
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
    private readonly List<int> _ports = [];
    private ServiceProvider? _agentServices;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public FakeVoiceHub VoiceHub { get; } = new(Roster);

    // The home behind the Home Assistant server: what a scenario reads to ask whether the change
    // it wanted happened, and whether anything else moved.
    public FakeHomeAssistant Home { get; } = new();

    // Home Assistant's websocket API, faked as its own server: the calendar's create and delete
    // are websocket commands, and a message handler cannot answer a socket upgrade. It shares the
    // home's calendar store and records into the home's call list.
    public FakeHomeAssistantSocket HomeSocket { get; private set; } = null!;

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

    // The sandbox container, real image and all: the one mount that advertises exec, without
    // which exec-shaped work has nowhere legitimate to go and the transfer rules have a single
    // writable mount to transfer between.
    public EvalSandbox Sandbox { get; private set; } = null!;

    // The workers, canned. A scenario about delegation is about the parent's decision, so what a
    // worker would have answered is a fixed string the scenario chooses rather than another run.
    public CannedSubAgents Workers { get; private set; } = null!;

    // One instant for the agent's decoration and for every server, so an expected fire time is an
    // exact string. It never advances: a clock that ticks would make a timer's remaining seconds a
    // range, and a scenario asserting a range asserts almost nothing.
    public FakeTimeProvider Clock { get; private set; } = null!;

    public IAgentFactory Factory { get; private set; } = null!;

    // The files the turn's channel holds, answered at the fetch hydration makes (EvalAttachments).
    public EvalAttachments Attachments { get; private set; } = null!;

    // Sugary's server, by the compose host name the sugary agent dials.
    public const string Sugary = "mcp-sugary";

    // Where Sugary's database and uploads rest for this run, deleted with the stack.
    private string? _sugaryPath;

    // The agent's exec bridge, hosted the way the deployment hosts it: the sandbox's daemons call
    // it for every file operation a command makes on the other mounts. One instance, registered
    // into the agent's services over the one AddAgent made, so the tokens the exec tool mints are
    // the ones this endpoint answers.
    public VfsBridge Bridge { get; } = new(TimeProvider.System, new VfsBridgeSettings());

    public static async Task<EvalStack> StartAsync(
        Scenario scenario, string redisConnectionString, Recording recording)
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
        stack.VoiceHub.Ringing = scenario.Turn.Ringing;
        stack.Attachments = new EvalAttachments(scenario.Turn);

        var shipped = ShippedSettings(redisConnectionString);

        // Every mount the agent dials, on every scenario, because what the timer contract
        // discriminates is *which* of them a request belongs in. A stack that hosted only the mount
        // the answer lands in would leave the model no wrong place to put it, and a discrimination
        // with one option is not one. What the agent does not dial is not started: its endpoint
        // would be dropped from the definition anyway, so it could only cost the run its time.
        var dialled = Dialled(shipped, scenario.AgentId);
        var hosted = new Dictionary<string, string>();
        if (dialled.Contains("mcp-sandbox"))
        {
            stack.Sandbox = await EvalSandbox.StartAsync(await stack.StartBridgeAsync());
            hosted["mcp-sandbox"] = stack.Sandbox.Endpoint;
        }

        if (dialled.Contains("mcp-timers"))
        {
            hosted["mcp-timers"] = await stack.StartTimersAsync(scenario);
        }

        if (dialled.Contains("mcp-scheduling"))
        {
            hosted["mcp-scheduling"] = await stack.StartSchedulingAsync(shipped, redisConnectionString);
        }

        if (dialled.Contains("mcp-homeassistant"))
        {
            hosted["mcp-homeassistant"] = await stack.StartHomeAssistantAsync();
        }

        if (dialled.Contains("mcp-vault"))
        {
            hosted["mcp-vault"] = await stack.StartVaultAsync();
        }

        if (dialled.Contains("mcp-websearch"))
        {
            hosted["mcp-websearch"] = await stack.StartWebSearchAsync(redisConnectionString);
        }

        if (dialled.Contains(Sugary))
        {
            hosted[Sugary] = await stack.StartSugaryAsync();
        }

        stack.BuildAgentServices(shipped, recording, scenario, hosted);

        return stack;
    }

    // The compose hosts a run of this agent can reach: its own servers, and its workers' when it
    // has any. By host name, the way Rewrite matches an endpoint to the server standing in for it.
    public static IReadOnlyList<string> Dialled(AgentSettings settings, string agentId)
    {
        var agent = settings.Agents.Single(a => a.Id == agentId);
        var workers = agent.EnabledFeatures.Any(feature =>
            feature.Equals("subagents", StringComparison.OrdinalIgnoreCase))
            ? settings.SubAgents.SelectMany(worker => worker.McpServerEndpoints)
            : [];

        return
        [
            .. agent.McpServerEndpoints.Concat(workers)
                .Select(endpoint => new Uri(endpoint).Host)
                .Distinct(StringComparer.Ordinal)
        ];
    }

    private async Task<string> StartBridgeAsync()
    {
        var (url, host, port) = await EvalBridgeHost.StartAsync(Bridge);
        _servers.Add(host);
        _ports.Add(port);
        return url;
    }

    private async Task<string> StartTimersAsync(Scenario scenario)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureTimers(new TimerSettings { Mcp = McpTestSecret.Gate });

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
            AllowedExtensions = [".md", ".txt", ".json", ".yaml", ".yml"],
            Mcp = McpTestSecret.Gate
        });

        return await StartAsync(builder, port);
    }

    // Home Assistant's own server, with the home behind it faked at the REST API. The catalog, the
    // action files and the argument parsing a scenario exercises are therefore the deployment's.
    private async Task<string> StartHomeAssistantAsync()
    {
        Music = await FakeMusicAssistantServer.StartAsync();
        HomeSocket = await Home.StartSocketAsync();

        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new HomeAssistantSettings
        {
            HomeAssistant = new HomeAssistantConfiguration
            {
                // The socket's address: REST never reaches it (the handler below answers first),
                // but the websocket client derives /api/websocket from this base.
                BaseUrl = HomeSocket.BaseUrl,
                Token = FakeHomeAssistant.Token
            },
            // Configured, so the server advertises the podcast-episode action — a deployment
            // without Music Assistant does not, and a scenario about episodes would then be
            // testing a toolset nobody runs.
            MusicAssistant = new MusicAssistantConfiguration
            {
                BaseUrl = Music.BaseUrl,
                Token = FakeMusicAssistantServer.ValidToken
            },
            Mcp = McpTestSecret.Gate
        });

        // The typed client keeps its name from the interface it is registered against, so this
        // reaches the same client the server resolves and replaces only its transport.
        builder.Services.AddHttpClient(nameof(IHomeAssistantClient))
            .ConfigurePrimaryHttpMessageHandler(() => Home);

        // The hub a watch's announcement target is resolved against, the same fake the timers
        // server rings: a room the roster does not have is refused here as it is in prod.
        builder.Services.AddHttpClient(VoiceHubHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => VoiceHub);

        // The scenario's instant for a listing's default window, a history's "now" and every
        // stamp on the mount; real timers, because a watch write retries on a delay.
        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new PinnedClock(Clock)));

        return await StartAsync(builder, port);
    }

    // The websearch server, with both of its externals replaced and nothing else: Brave answers
    // from a table, and the browser is a local Chromium rather than the remote hardened Firefox a
    // deployment connects to. Everything between them — the tools, the session manager, the
    // accessibility snapshot, the modal dismisser — is the deployment's own.
    private async Task<string> StartWebSearchAsync(string redisConnectionString)
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
            },
            Mcp = McpTestSecret.Gate,
            // The run's user is a person of this deployment, so a turn can browse as them and
            // hand them a login; the link's address is a placeholder nothing opens. No state key:
            // nothing is remembered between runs. The handoff's links live on the run's Redis.
            RedisConnectionString = redisConnectionString,
            Identity = new IdentityConfiguration
            {
                People = [new PersonIdentity { Name = "fran", Telegram = EvalRun.UserId, WebChat = EvalRun.UserId }]
            },
            Handoff = new HandoffConfiguration { PublicBaseUrl = EvalWeb.HandoffBase }
        });

        // The typed client keeps its name from the interface it is registered against.
        builder.Services.AddHttpClient(nameof(IWebSearchClient))
            .ConfigurePrimaryHttpMessageHandler(() => Web.SearchTransport);

        // The one browser behind every role the module gives it — the tabs browsed, the tabs a
        // person logs in on, their contexts — so a handoff opens in the browser the turn reads.
        builder.Services.Replace(ServiceDescriptor.Singleton(sp => new PlaywrightWebBrowser(
            browserFactory: LaunchAsync, personStates: sp.GetRequiredService<IPersonStateStore>())));

        return await StartAsync(builder, port);
    }

    // One browser per stack, launched the way every web fixture launches one.
    private async Task<IBrowser> LaunchAsync()
    {
        _playwright ??= await Playwright.CreateAsync();
        _browser = await EvalWeb.LaunchBrowserAsync(_playwright);

        return _browser;
    }

    // Sugary's own server, module and all, on a database and an upload directory of this run's
    // own: the sugary agent's estimating prompt and its skill are served from here, so a stack
    // without it would measure the agent with neither. Her log is seeded through the server's
    // store, so /sugary renders it as it renders hers.
    private async Task<string> StartSugaryAsync()
    {
        _sugaryPath = Path.Combine(Path.GetTempPath(), $"eval-sugary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sugaryPath);

        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureSugary(EvalSugaryLog.Settings(_sugaryPath));

        var endpoint = await StartAsync(builder, port);
        await EvalSugaryLog.SeedAsync(_servers[^1].Services);

        return endpoint;
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
            Delivery = new DeliverySettings { DefaultDeliverTo = ["signalr"] },
            Mcp = McpTestSecret.Gate
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
        _ports.Add(port);

        return $"http://localhost:{port}/mcp";
    }

    // The shipped configuration, with four edits and no fifth: the secrets come from user
    // secrets rather than from the environment the container would have had, nothing dials a
    // channel — the boundary of an eval is the agent, and what a channel does with a reply
    // afterwards is covered by the channel and end-to-end suites — the model is whatever
    // ZIGGURAT_EVAL_MODEL asks for, so a pass against another model needs no edit to the file, and
    // the deployment secret is the one every server this stack hosts is gated behind.
    // Bound once per process, so an edit to the file mid-pass reaches no stack of it.
    private static AgentSettings ShippedSettings(string redisConnectionString) =>
        EvalModel.FromEnvironment(ShippedDefinition.Repository.Settings) with
        {
            Redis = new RedisConfiguration { ConnectionString = redisConnectionString },
            ChannelEndpoints = [],
            Mcp = McpTestSecret.Gate
        };

    // The agent itself, built by the real factory. Only the configured endpoint urls are rewritten,
    // to the servers this stack hosts.
    private void BuildAgentServices(
        AgentSettings shipped, Recording recording, Scenario scenario,
        IReadOnlyDictionary<string, string> hosted)
    {
        var settings = scenario.Workers == WorkerMode.None
            ? Alone(Rehosted(shipped, hosted), scenario.AgentId)
            : Rehosted(shipped, hosted);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgent(settings);
        services.AddSubAgents(settings.SubAgents);
        services.AddSingleton<IToolInvocationObserver>(recording);
        // Registered after the agent's own publisher so it wins the resolution: what the chat
        // client reports as usage is the run's bill, and the recording is where the scorecard
        // reads it from.
        services.AddSingleton<IMetricsPublisher>(recording);
        // Absent where a scenario asks for workers that really run: the factory then builds
        // them itself, as it does in every deployment, on the parent's model and this stack's
        // servers, and what was delegated is read off the parent's own calls.
        if (scenario.Workers == WorkerMode.Canned)
        {
            services.AddSingleton<ISubAgentSpawner>(Workers);
        }

        services.AddSingleton<IVfsBridge>(Bridge);
        // After the agent's own source, so it wins the resolution: the channel's fetch, answered
        // from the scenario's files because nothing here dials the channel that would hold them.
        services.AddSingleton<IAttachmentSource>(Attachments);
        // The scenario's instant, so the prompt's "today is" is the day the turn is stamped with.
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new PinnedClock(Clock)));

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
    private static string[] Rewrite(IEnumerable<string> endpoints, IReadOnlyDictionary<string, string> hosted) =>
        [.. endpoints
            .Select(endpoint => hosted.FirstOrDefault(h => endpoint.Contains(h.Key)).Value)
            .Where(rewritten => rewritten is not null)];

    // One agent without the feature that gives it workers: no tool to call them with and no
    // section telling it they exist. Everything else about it, and everything about every other
    // agent, is the shipped definition.
    public static AgentSettings Alone(AgentSettings settings, string agentId) =>
        settings with
        {
            Agents =
            [
                .. settings.Agents.Select(a => a.Id == agentId
                    ? a with
                    {
                        EnabledFeatures =
                        [
                            .. a.EnabledFeatures.Where(feature =>
                                !feature.Equals("subagents", StringComparison.OrdinalIgnoreCase))
                        ]
                    }
                    : a)
            ]
        };

    // A worker's endpoints as much as an agent's. A canned worker dials nothing and never
    // noticed; one that really runs dials what its own profile names.
    public static AgentSettings Rehosted(AgentSettings shipped, IReadOnlyDictionary<string, string> hosted) =>
        shipped with
        {
            Agents =
            [
                .. shipped.Agents.Select(a => a with { McpServerEndpoints = Rewrite(a.McpServerEndpoints, hosted) })
            ],
            SubAgents =
            [
                .. shipped.SubAgents.Select(w => w with { McpServerEndpoints = Rewrite(w.McpServerEndpoints, hosted) })
            ]
        };

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_servers.Select(async server =>
        {
            await server.StopAsync();
            server.Dispose();
        }));

        // Only once nothing binds them any more: a full tier is more runs than the band has
        // ports, so a stack that kept its numbers for the process's lifetime exhausted it.
        _ports.ForEach(TestPort.Release);

        if (_agentServices is not null)
        {
            await _agentServices.DisposeAsync();
        }

        // Only what this run started: a stack stands up the servers its agent dials and no more.
        if (Music is not null)
        {
            await Music.DisposeAsync();
        }

        if (HomeSocket is not null)
        {
            await HomeSocket.DisposeAsync();
        }

        if (Web is not null)
        {
            await Web.DisposeAsync();
        }

        if (Sandbox is not null)
        {
            await Sandbox.DisposeAsync();
        }

        if (_sugaryPath is not null && Directory.Exists(_sugaryPath))
        {
            // The server's pooled connections would otherwise stay open on a deleted file, one set
            // per stack, for the rest of a pass that stands up a stack per meal.
            SqliteConnection.ClearAllPools();
            Directory.Delete(_sugaryPath, recursive: true);
        }

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