using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using McpServerSugary.App;
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

namespace Tests.Integration.Fixtures;

// The Sugary server booted through its shipped module and its app's routes, on a database of its
// own in a directory of its own. One per test rather than one per class: what a mount lists depends
// on every meal in the database, so a sibling's meal would answer for this test's months.
public sealed class SugaryServer : IAsyncDisposable
{
    public const string AccessCode = "tortilla";

    private readonly RunningServer _server;
    private readonly List<HttpClient> _browsers = [];

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

    // Open Food Facts is never called from a test: a lookup goes wherever `openFoodFacts` points, a
    // stub, and with none it goes to an address nothing listens on.
    public static async Task<SugaryServer> StartAsync(
        string homeZone = "Europe/Madrid", string accessCode = AccessCode, string? openFoodFacts = null,
        string? webRoot = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settings = new SugarySettings
        {
            DatabasePath = Path.Combine(directory, "sugary.db"),
            HomeZone = homeZone,
            Mcp = McpTestSecret.Gate,
            SugaryApp = new SugaryAppSettings { AccessCode = accessCode },
            OpenFoodFactsUrl = openFoodFacts ?? "http://127.0.0.1:9/"
        };

        var server = await InMemoryMcpServer.StartAsync(
            services => services.ConfigureSugary(settings), map: app => app.MapSugaryApp(), webRoot: webRoot);
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
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}