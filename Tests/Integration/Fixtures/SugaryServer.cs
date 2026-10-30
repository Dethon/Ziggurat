using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.Security;
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
using SugarySettings = McpServerSugary.Settings.SugarySettings;
using UploadSettings = McpServerSugary.Settings.UploadSettings;

namespace Tests.Integration.Fixtures;

// The Sugary server booted through its shipped module and its app's routes, on a database of its
// own in a directory of its own. One per test rather than one per class: what a mount lists depends
// on every meal in the database, so a sibling's meal would answer for this test's months.
public sealed class SugaryServer : IAsyncDisposable
{
    // The people the proxy's session check names, as opaque as the accounts service's own ids: two
    // of one household, who share its kitchen and keep their own logs, and one of another.
    public static readonly Account Her = new("household-home", "user-her");
    public static readonly Account Him = new("household-home", "user-him");
    public static readonly Account Neighbour = new("household-next-door", "user-neighbour");

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

    // Where an agent dials it, as a definition names an endpoint.
    public string Endpoint => _server.Endpoint;

    // A conversation no estimate ever has, for the probe AgentAsync sends.
    private const string ProbeConversation = "probe";

    // Open Food Facts is never called from a test: a lookup goes wherever `openFoodFacts` points, a
    // stub, and with none it goes to an address nothing listens on. A test that waits out a turn
    // hands its clock to the one service that waits on the agent, and to nothing else: the channel
    // inbox decides liveness on the clock it is given, and a fake one advanced past a turn's wait
    // would make an agent polling in real time look gone.
    public static async Task<SugaryServer> StartAsync(
        string homeZone = "Europe/Madrid", string? openFoodFacts = null,
        string? webRoot = null, TimeProvider? turnClock = null, TimeProvider? estimateClock = null,
        Func<SugarySettings, SugarySettings>? configure = null, Action<IServiceCollection>? services = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settings = new SugarySettings
        {
            DatabasePath = Path.Combine(directory, "sugary.db"),
            HomeZone = homeZone,
            Mcp = McpTestSecret.Gate,
            OpenFoodFactsUrl = openFoodFacts ?? "http://127.0.0.1:9/",
            Uploads = new UploadSettings { StoragePath = Path.Combine(directory, "uploads") }
        };
        settings = configure?.Invoke(settings) ?? settings;

        var server = await InMemoryMcpServer.StartAsync(
            collection =>
            {
                collection.ConfigureSugary(settings);
                if (turnClock is not null)
                {
                    collection.AddSingleton(sp => ActivatorUtilities.CreateInstance<SugaryChannel>(sp, turnClock));
                }

                // How long her answers may follow an estimate, on a clock of its own for the same
                // reason: nothing else may see time jump.
                if (estimateClock is not null)
                {
                    collection.AddSingleton(sp => ActivatorUtilities.CreateInstance<OpenEstimates>(sp, estimateClock));
                }

                // A test's own stand-ins, registered last so they win.
                services?.Invoke(collection);
            },
            map: app => app.MapSugaryApp(), webRoot: webRoot);
        return new SugaryServer(server, settings, directory);
    }

    // A request that did not come through the proxy: it carries nobody's headers.
    public HttpClient Browser()
    {
        var browser = new HttpClient { BaseAddress = Address };
        _browsers.Add(browser);
        return browser;
    }

    // A phone signed in as this person, as the proxy hands its requests on: the session check's
    // three headers on every request, and nothing else that says who it is.
    public HttpClient As(Account person, string roles = "member")
    {
        var browser = Browser();
        browser.DefaultRequestHeaders.Add(ForwardedAccount.UserHeader, person.UserId);
        browser.DefaultRequestHeaders.Add(ForwardedAccount.TenantHeader, person.TenantId);
        browser.DefaultRequestHeaders.Add(ForwardedAccount.RolesHeader, roles);
        return browser;
    }

    // Her phone, signed in: the person most of the suite's logs belong to.
    public Task<HttpClient> SignedInAsync() => Task.FromResult(As(Her));

    // The agent as the app reaches it: the agent host's own channel connection for `sugary`,
    // dialled at this server, so a test receives each turn and sends each reply chunk exactly as the
    // agent does. Gate-on-live delivers only to an agent that has polled, and the connection's first
    // poll lands on its own schedule, so the server is asked until it says the agent is listening —
    // with a cancel for a conversation nobody has, which reaches the agent as one message and is read
    // off here so the first thing a test receives is its own turn.
    public async Task<McpChannelConnection> AgentAsync()
    {
        var agent = new McpChannelConnection("sugary", mcpSecret: McpTestSecret.Value, callTokens: McpTestSecret.CallTokens);
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
        await using var db = Hers();
        db.Meals.AddRange(meals);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(params Food[] foods)
    {
        await using var db = Hers();
        db.Foods.AddRange(foods);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(params Dose[] doses)
    {
        await using var db = Hers();
        db.Doses.AddRange(doses);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(params ExerciseSession[] sessions)
    {
        await using var db = Hers();
        db.ExerciseSessions.AddRange(sessions);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(params Period[] periods)
    {
        await using var db = Hers();
        db.Periods.AddRange(periods);
        await db.SaveChangesAsync();
    }

    public async Task SaveAsync(params DayNote[] notes)
    {
        await using var db = Hers();
        db.DayNotes.AddRange(notes);
        await db.SaveChangesAsync();
    }

    // What the migration into the home household does with a log from before it was per person (or
    // brought in by the import): gives every row nobody owns to the person whose log it is, and the
    // kitchen to their household. Until then the rows are kept and seen by nobody.
    public async Task GiveUnassignedRowsToAsync(Account person)
    {
        await using var db = await _server.App.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>()
            .CreateDbContextAsync();
        await db.Meals.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.UserId, person.UserId).SetProperty(r => r.TenantId, person.TenantId));
        await db.Doses.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.UserId, person.UserId).SetProperty(r => r.TenantId, person.TenantId));
        await db.Pens.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.UserId, person.UserId).SetProperty(r => r.TenantId, person.TenantId));
        await db.PushSubscriptions.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned).ExecuteUpdateAsync(
            set => set.SetProperty(r => r.UserId, person.UserId).SetProperty(r => r.TenantId, person.TenantId));
        await db.ExerciseSessions.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned).ExecuteUpdateAsync(
            set => set.SetProperty(r => r.UserId, person.UserId).SetProperty(r => r.TenantId, person.TenantId));
        await db.Periods.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.UserId, person.UserId).SetProperty(r => r.TenantId, person.TenantId));
        await db.DayNotes.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.UserId, person.UserId).SetProperty(r => r.TenantId, person.TenantId));
        await db.Foods.IgnoreQueryFilters().Where(r => r.TenantId == Owners.Unassigned).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.TenantId, person.TenantId));
        await db.Recipes.IgnoreQueryFilters().Where(r => r.TenantId == Owners.Unassigned).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.TenantId, person.TenantId));
    }

    // A tool called as the agent calls it on a turn: the turn's conversation context on `_meta`, its
    // principal carrying the account of the person whose turn it is — hers, unless a test names
    // another turn, and none at all when a test passes null.
    public Task<JsonNode> CallAsync(string tool, Dictionary<string, object?> arguments) =>
        CallAsync(tool, arguments, TurnOf(Her));

    public async Task<JsonNode> CallAsync(
        string tool, Dictionary<string, object?> arguments, ConversationContext? turn)
    {
        var result = await Client.CallToolAsync(new CallToolRequestParams
        {
            Name = tool,
            Arguments = arguments.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value)),
            Meta = turn is null
                ? null
                : new JsonObject
                {
                    [ChannelProtocol.ConversationContextMetaKey] =
                        JsonSerializer.SerializeToNode(turn, ChannelProtocol.SerializerOptions)
                }
        });
        return JsonNode.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))!;
    }

    // A chat-client turn of this person's, its principal as the hub stamps it.
    public static ConversationContext TurnOf(Account person) => new(
        "jonas", "conversation-1", person.UserId, new ReplyTarget("signalr", "conversation-1"),
        Principal: new Principal("signalr", person.UserId, person));

    // The file's text exactly as the mount serves it.
    public async Task<string> ReadAsync(string path)
    {
        var read = await CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = path });
        return read["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException($"reading {path} failed: {read.ToJsonString()}");
    }

    // A table as the app's settings download it: the CSV an import reads back.
    public async Task<string> ExportAsync(string table, string tz = "Europe/Madrid")
    {
        var client = await SignedInAsync();
        var response = await client.GetAsync($"/api/export/{table}.csv?tz={tz}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    // Her log asked over MCP, as the agent's query tool asks it on her turn; the rows, or the call
    // failing loudly.
    public Task<JsonArray> QueryAsync(string sql) => QueryAsync(sql, TurnOf(Her));

    public async Task<JsonArray> QueryAsync(string sql, ConversationContext? turn)
    {
        var answer = await CallAsync(
            "fs_query", new Dictionary<string, object?> { ["path"] = "/", ["sql"] = sql }, turn);
        return answer["rows"]?.AsArray()
               ?? throw new InvalidOperationException($"query failed: {answer.ToJsonString()}");
    }

    // A table's file as a command reads it: its bytes over the byte read, opened by DuckDB as any
    // Parquet file. Every value comes back as text, times on UTC's clock.
    public async Task<IReadOnlyList<string?[]>> TableRowsAsync(string path)
    {
        var bytes = new List<byte>();
        var eof = false;
        while (!eof)
        {
            var chunk = await CallAsync("fs_blob_read", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["offset"] = bytes.Count
            });
            bytes.AddRange(Convert.FromBase64String(chunk["contentBase64"]?.GetValue<string>()
                                                    ?? throw new InvalidOperationException(chunk.ToJsonString())));
            eof = chunk["eof"]!.GetValue<bool>();
        }

        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sugary-{Guid.NewGuid():N}.parquet");
        await File.WriteAllBytesAsync(file, bytes.ToArray());
        try
        {
            await using var connection = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET TimeZone = 'UTC'; select columns(*)::varchar from read_parquet('{file}')";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string?[]>();
            while (await reader.ReadAsync())
            {
                rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetString(i))]);
            }

            return rows;
        }
        finally
        {
            File.Delete(file);
        }
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

    public IServiceProvider Services => _server.App.Services;

    // Her log as the app would have written it: rows of hers, and foods of her household's.
    private SugaryDbContext Hers() => _server.App.Services.GetRequiredService<SugaryStore>().For(Her);

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