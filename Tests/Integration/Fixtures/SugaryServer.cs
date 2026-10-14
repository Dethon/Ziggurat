using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Mcp.Hosting;
using McpServerSugary.App;
using McpServerSugary.Channel;
using McpServerSugary.Modules;
using McpServerSugary.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.McpServers;
using SugaryAppSettings = McpServerSugary.Settings.SugaryAppSettings;
using SugarySettings = McpServerSugary.Settings.SugarySettings;
using UploadSettings = McpServerSugary.Settings.UploadSettings;

namespace Tests.Integration.Fixtures;

// The Sugary server booted through its shipped module and its app's routes, on a database of its
// own in a directory of its own. One per test rather than one per class: what a mount lists depends
// on every meal in the database, so a sibling's meal would answer for this test's months.
public sealed class SugaryServer : IAsyncDisposable
{
    public const string AccessCode = "tortilla";

    private readonly RunningServer _server;
    private readonly List<HttpClient> _browsers = [];
    private readonly List<McpChannelConnection> _agents = [];

    private SugaryServer(RunningServer server, SugarySettings settings, string directory)
    {
        _server = server;
        Settings = settings;
        Directory = directory;
    }

    public SugarySettings Settings { get; }

    // Where the database lives, and the only place the server may write.
    public string Directory { get; }

    public McpClient Client => _server.Client;

    public Uri Address => new(new Uri(_server.Endpoint), "/");

    // A conversation no estimate ever has, for the probe AgentAsync sends.
    private const string ProbeConversation = "probe";

    // Open Food Facts is never called from a test: a lookup goes wherever `openFoodFacts` points, a
    // stub, and with none it goes to an address nothing listens on. A test that waits out a turn
    // hands its clock to the one service that waits on the agent, and to nothing else: the channel
    // inbox decides liveness on the clock it is given, and a fake one advanced past a turn's wait
    // would make an agent polling in real time look gone.
    public static async Task<SugaryServer> StartAsync(
        string homeZone = "Europe/Madrid", string accessCode = AccessCode, string? openFoodFacts = null,
        string? webRoot = null, TimeProvider? turnClock = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settings = new SugarySettings
        {
            DatabasePath = Path.Combine(directory, "sugary.db"),
            HomeZone = homeZone,
            Mcp = McpTestSecret.Gate,
            SugaryApp = new SugaryAppSettings { AccessCode = accessCode },
            OpenFoodFactsUrl = openFoodFacts ?? "http://127.0.0.1:9/",
            Uploads = new UploadSettings { StoragePath = Path.Combine(directory, "uploads") }
        };

        var server = await InMemoryMcpServer.StartAsync(
            services =>
            {
                services.ConfigureSugary(settings);
                if (turnClock is not null)
                {
                    services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SugaryChannel>(sp, turnClock));
                }
            },
            map: app => app.MapSugaryApp(), webRoot: webRoot);
        return new SugaryServer(server, settings, directory);
    }

    // A phone that has not signed in: its own cookie jar, nothing in it yet.
    public HttpClient Browser()
    {
        var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = Address
        };
        _browsers.Add(browser);
        return browser;
    }

    // A phone she has typed the code into, as the app's sign-in screen does.
    public async Task<HttpClient> SignedInAsync()
    {
        var browser = Browser();
        var response = await browser.PostAsJsonAsync("/api/session", new { code = AccessCode });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return browser;
    }

    // The agent as the app reaches it: the agent host's own channel connection for `sugary`,
    // dialled at this server, so a test receives each turn and sends each reply chunk exactly as the
    // agent does. Gate-on-live delivers only to an agent that has polled, and the connection's first
    // poll lands on its own schedule, so the server is asked until it says the agent is listening —
    // with a cancel for a conversation nobody has, which reaches the agent as one message and is read
    // off here so the first thing a test receives is its own turn.
    public async Task<McpChannelConnection> AgentAsync()
    {
        var agent = new McpChannelConnection("sugary", mcpSecret: McpTestSecret.Value);
        _agents.Add(agent);
        await agent.ConnectAsync(_server.Endpoint, CancellationToken.None);

        var emitter = _server.App.Services.GetRequiredService<ChannelNotificationEmitter>();
        await Eventually.Until(
            async ValueTask<bool> () => await emitter.EmitCancelAsync(
                new ChannelCancelNotification { ConversationId = ProbeConversation }),
            "the agent to be polling");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (await agent.Messages.FirstAsync(cts.Token)).ConversationId.ShouldBe(ProbeConversation);
        return agent;
    }

    // What the app does when she saves: a row in the database, nothing else.
    public async Task SaveAsync(params Meal[] meals)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        db.Meals.AddRange(meals);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(params Food[] foods)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        db.Foods.AddRange(foods);
        await db.SaveChangesAsync();
    }

    public async Task<JsonNode> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await Client.CallToolAsync(tool, arguments);
        return JsonNode.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))!;
    }

    // The file's text exactly as the mount serves it.
    public async Task<string> ReadAsync(string path)
    {
        var read = await CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = path });
        return read["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException($"reading {path} failed: {read.ToJsonString()}");
    }

    public async Task<IReadOnlyList<string>> GlobAsync(string pattern, string basePath = "")
    {
        var glob = await CallAsync("fs_glob", new Dictionary<string, object?>
        {
            ["pattern"] = pattern,
            ["basePath"] = basePath
        });
        return glob["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
    }

    private IDbContextFactory<SugaryDbContext> Contexts =>
        _server.App.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>();

    public async ValueTask DisposeAsync()
    {
        _browsers.ForEach(browser => browser.Dispose());
        foreach (var agent in _agents)
        {
            await agent.DisposeAsync();
        }

        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}