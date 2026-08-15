using Mcp.Hosting;
using McpChannelServiceBus.Modules;
using McpChannelSignalR.Modules;
using McpChannelTelegram.Modules;
using McpChannelVoice.Modules;
using McpServerHomeAssistant.Modules;
using McpServerIdealista.Modules;
using McpServerLibrary.Modules;
using McpServerPrinter.Modules;
using McpServerSandbox.Modules;
using McpServerScheduling.Modules;
using McpServerTimers.Modules;
using McpServerVault.Modules;
using McpServerWebSearch.Modules;
using Microsoft.Extensions.DependencyInjection;
using HaSettings = McpServerHomeAssistant.Settings;
using IdealistaSettings = McpServerIdealista.Settings;
using LibrarySettings = McpServerLibrary.Settings;
using PrinterSettings = McpServerPrinter.Settings;
using SandboxSettings = McpServerSandbox.Settings;
using SchedulingSettings = McpServerScheduling.Settings;
using ServiceBusSettings = McpChannelServiceBus.Settings;
// Aliased because Tests.Integration has sibling namespaces named after the server projects, which
// shadow the project namespaces from inside Tests.
using SignalRSettings = McpChannelSignalR.Settings;
using TelegramSettings = McpChannelTelegram.Settings;
using TimersSettings = McpServerTimers.Settings;
using VaultSettings = McpServerVault.Settings;
using VoiceSettings = McpChannelVoice.Settings;
using WebSearchSettings = McpServerWebSearch.Settings;

namespace Tests.Integration.McpServers;

public enum McpServerRole
{
    // Offers the agent things to call and nothing it can be pushed from.
    Tool,

    // The agent can be pushed from it, and a reply comes back the same way.
    Channel,

    // Both at once: tools for the agent, plus something it can raise unprompted.
    DualRole
}

// ProjectDirectory is the folder the server's shipped appsettings.json lives in, which is the only
// thing about a server that cannot be reached from its settings object or its module.
public sealed record McpServerRow(
    string Id,
    string ProjectDirectory,
    McpServerRole Role,
    object Settings,
    Action<IServiceCollection> Configure,
    DeliveryPolicy? Policy = null)
{
    public override string ToString() => Id;
}

// Every MCP server in the repo, each row driving that server's REAL registration entry point — the
// ConfigModule that ships. Hand-registering an equivalent here instead would stay green against a
// module that forgot something, which is the whole failure this table exists to catch.
//
// This is the one place in the repo where servers are compared to each other. Adding a server means
// adding a row; the test bodies in McpServerContractTests and ChannelReceiveContractTests are
// written once and never copied.
public static class McpServerRegistrations
{
    // ConfigureChannel eagerly calls ConnectionMultiplexer.Connect. abortConnect=false makes that
    // hand back a disconnected multiplexer instead of throwing, so the registration theory runs
    // without a Redis container; port 1 is refused instantly and the short timeouts keep the
    // background reconnect loop from lingering.
    public const string UnreachableRedis =
        "127.0.0.1:1,abortConnect=false,connectTimeout=100,connectRetry=1";

    // ConfigureChannel eagerly constructs a ServiceBusClient from this, which parses the
    // connection string without ever dialing out. Well-formed but unreachable — SharedAccessKey
    // just needs to be valid base64.
    public const string FakeServiceBusConnectionString =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=Zm9vYmFyMTIzNDU2Nzg5MA==";

    // The declared delivery policy is part of the row, so this table is the single place where
    // "this is a channel server, and this is the policy it chose" is verified against the real
    // registration. That is the check that would have caught all three rounds of the
    // stale-subscriber defect: every one of them was a server buffering differently from what its
    // caller assumed, and none of them was visible anywhere but inside the emitter.
    //
    // Broadcast for the two chat-shaped transports whose callers cannot retry; gate-on-live for
    // the three whose callers settle a durable record on a confirmed delivery — a schedule, a
    // routing entry, a broker message — and would otherwise keep the record *and* leave a buffered
    // duplicate behind; buffer-always for the one transport with no way to tell a sender to try
    // again later.
    //
    // The tool servers all take unreachable-but-well-formed connection details for the same reason
    // the channel rows do: registration must run with no container and no network.
    public static IReadOnlyList<McpServerRow> All =>
    [
        Row("signalr", "McpChannelSignalR", McpServerRole.Channel,
            new SignalRSettings.ChannelSettings { RedisConnectionString = UnreachableRedis },
            (services, settings) => services.ConfigureChannel(settings),
            DeliveryPolicy.Broadcast),

        Row("telegram", "McpChannelTelegram", McpServerRole.Channel,
            new TelegramSettings.ChannelSettings { Bots = [], AllowedUsernames = [] },
            (services, settings) => services.ConfigureChannel(settings),
            DeliveryPolicy.BufferAlways),

        Row("servicebus", "McpChannelServiceBus", McpServerRole.Channel,
            new ServiceBusSettings.ChannelSettings
            {
                ServiceBusConnectionString = FakeServiceBusConnectionString,
                PromptQueueName = "prompts",
                ResponseQueueName = "responses"
            },
            (services, settings) => services.ConfigureChannel(settings),
            DeliveryPolicy.GateOnLive),

        // ConfigureVoiceChannel's IConnectionMultiplexer registration is a lazy factory (unlike
        // SignalR's eager Connect), and nothing in this registration-only test resolves it, so no
        // live/fake Redis endpoint is needed — defaults are enough, exactly as ConfigModuleTests
        // already relies on for the rest of the voice DI graph.
        Row("voice", "McpChannelVoice", McpServerRole.Channel,
            new VoiceSettings.VoiceSettings(),
            (services, settings) => services.ConfigureVoiceChannel(settings),
            DeliveryPolicy.Broadcast),

        // Same lazy IConnectionMultiplexer factory as voice, so the connection string just needs to
        // satisfy the required property.
        Row("scheduling", "McpServerScheduling", McpServerRole.DualRole,
            new SchedulingSettings.SchedulingSettings { RedisConnectionString = UnreachableRedis },
            (services, settings) => services.ConfigureScheduling(settings),
            DeliveryPolicy.GateOnLive),

        // AddJacketClient/AddQBittorrentClient only register typed HttpClients (no eager dial), so
        // plain placeholder settings are enough to build the container.
        Row("library", "McpServerLibrary", McpServerRole.DualRole,
            new LibrarySettings.McpSettings
            {
                Jackett = new LibrarySettings.JackettConfiguration { ApiKey = "x", ApiUrl = "http://jackett" },
                QBittorrent = new LibrarySettings.QBittorrentConfiguration
                {
                    ApiUrl = "http://qbittorrent", UserName = "x", Password = "x"
                },
                DownloadLocation = "/downloads",
                BaseLibraryPath = "/media",
                RedisConnectionString = UnreachableRedis
            },
            (services, settings) => services.ConfigureMcp(settings),
            DeliveryPolicy.GateOnLive),

        // MusicAssistant left absent: the podcast-episode action is the one conditional branch in
        // this module, and the row that exercises the default is the one every deployment runs.
        Row("homeassistant", "McpServerHomeAssistant", McpServerRole.Tool,
            new HaSettings.McpSettings
            {
                HomeAssistant = new HaSettings.HomeAssistantConfiguration
                {
                    BaseUrl = "http://home-assistant:8123", Token = "x"
                }
            },
            (services, settings) => services.ConfigureMcp(settings)),

        Row("idealista", "McpServerIdealista", McpServerRole.Tool,
            new IdealistaSettings.McpSettings
            {
                Idealista = new IdealistaSettings.IdealistaConfiguration { ApiKey = "x", ApiSecret = "x" }
            },
            (services, settings) => services.ConfigureMcp(settings)),

        // The spool path is never touched during registration: PrintSpool creates its directory on
        // the first write, and nothing here writes.
        Row("printer", "McpServerPrinter", McpServerRole.Tool,
            new PrinterSettings.PrinterSettings { PrinterUri = "ipp://printer:631/ipp/print" },
            (services, settings) => services.ConfigurePrinter(settings)),

        Row("sandbox", "McpServerSandbox", McpServerRole.Tool,
            new SandboxSettings.McpSettings
            {
                // A non-"/" root on purpose: the workspace the conformance test pins is derived
                // relative to the configured root, and this pairing is the one a trimmed absolute
                // HomeDir used to get wrong.
                ContainerRoot = "/srv/jail",
                HomeDir = "/srv/jail/home/sandbox_user",
                DefaultTimeoutSeconds = 30,
                MaxTimeoutSeconds = 300,
                OutputCapBytes = 65536
            },
            (services, settings) => services.ConfigureMcp(settings)),

        // The voice hub is reached through a named HttpClient, which never dials at registration.
        Row("timers", "McpServerTimers", McpServerRole.Tool,
            new TimersSettings.TimerSettings(),
            (services, settings) => services.ConfigureTimers(settings)),

        Row("vault", "McpServerVault", McpServerRole.Tool,
            new VaultSettings.McpSettings { VaultPath = "/vault", AllowedExtensions = [".md"] },
            (services, settings) => services.ConfigureMcp(settings)),

        // CapSolver and Camoufox left absent, which is how a deployment without them runs: the
        // solver client is not registered and the browser gets a null endpoint. PlaywrightWebBrowser
        // launches nothing until its first navigation.
        Row("websearch", "McpServerWebSearch", McpServerRole.Tool,
            new WebSearchSettings.McpSettings
            {
                BraveSearch = new WebSearchSettings.BraveSearchConfiguration { ApiKey = "x" }
            },
            (services, settings) => services.ConfigureMcp(settings))
    ];

    public static IReadOnlyList<McpServerRow> ChannelServers =>
        All.Where(row => row.Role is McpServerRole.Channel or McpServerRole.DualRole).ToList();

    public static McpServerRow Get(string id) => All.Single(row => row.Id == id);

    // The working tree, never AppContext.BaseDirectory: a server's shipped files live beside its
    // csproj, and every one of these projects copies its appsettings.json to the same test output,
    // so Tests/bin/.../appsettings.json is whichever project won the copy race.
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ProjectPath(McpServerRow row) =>
        Path.Combine(RepoRoot, row.ProjectDirectory);

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ziggurat.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Ziggurat.sln not found above test directory");
    }

    public static TheoryData<string> Ids(IEnumerable<McpServerRow> rows) =>
        rows.Aggregate(new TheoryData<string>(), (data, row) =>
        {
            data.Add(row.Id);
            return data;
        });

    private static McpServerRow Row<TSettings>(
        string id,
        string projectDirectory,
        McpServerRole role,
        TSettings settings,
        Action<IServiceCollection, TSettings> configure,
        DeliveryPolicy? policy = null)
        where TSettings : class =>
        new(id, projectDirectory, role, settings, services => configure(services, settings), policy);
}