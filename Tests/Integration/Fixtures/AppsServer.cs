using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using McpServerApps.Modules;
using McpServerApps.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tests.Integration.McpServers;

namespace Tests.Integration.Fixtures;

// The apps server booted through its shipped module, on a volume of its own, its households'
// agent names answered without an accounts service. One per test: what /apps lists is every app in
// the volume, so a sibling's apps would answer for this test's.
public sealed class AppsServer : IAsyncDisposable
{
    public const string AppsDomain = "apps.test";

    // Two people of one household, who keep their own apps, and one of another.
    public static readonly Account Her = new("household-home", "user-her");
    public static readonly Account Him = new("household-home", "user-him");
    public static readonly Account Neighbour = new("household-next-door", "user-neighbour");

    // The agents every household runs, as the accounts service's agent-names view lists them.
    public static readonly IReadOnlyList<HouseholdAgentName> HouseholdAgents =
        [new("jonas", "Jonas"), new("sugary", "Sugary")];

    private readonly RunningServer _server;

    private AppsServer(RunningServer server, AppsSettings settings)
    {
        _server = server;
        Settings = settings;
    }

    public AppsSettings Settings { get; }

    public string Endpoint => _server.Endpoint;

    public static async Task<AppsServer> StartAsync(Func<AppsSettings, AppsSettings>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "apps-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new AppsSettings { StoragePath = directory, AppsDomain = AppsDomain, Mcp = McpTestSecret.Gate };
        settings = configure?.Invoke(settings) ?? settings;
        var server = await InMemoryMcpServer.StartAsync(collection =>
        {
            collection.ConfigureApps(settings);
            collection.Replace(ServiceDescriptor.Singleton<IHouseholdConfiguration>(
                new EveryHouseholdIn(TimeZoneInfo.Utc, HouseholdAgents)));
        });
        return new AppsServer(server, settings);
    }

    // A chat-client turn of this person's on one of the household's agents, its principal as the hub
    // stamps it; the test transport signs the call for that person.
    public static ConversationContext TurnOf(Account person, string agentId = "jonas") => new(
        agentId, "conversation-1", person.UserId, new ReplyTarget("signalr", "conversation-1"),
        Principal: new Principal("signalr", person.UserId, person));

    // A tool called as the agent calls it on a turn; null calls it for nobody.
    public async Task<JsonNode> CallAsync(string tool, Dictionary<string, object?> arguments, ConversationContext? turn)
    {
        var result = await _server.Client.CallToolAsync(new CallToolRequestParams
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

    public Task<JsonNode> CreateAsync(Account person, string path, string content, bool overwrite = false) =>
        CallAsync("fs_create", new Dictionary<string, object?>
        {
            ["path"] = path, ["content"] = content, ["overwrite"] = overwrite, ["createDirectories"] = true
        }, TurnOf(person));

    public async Task<IReadOnlyList<string>> GlobAsync(Account person, string pattern, string basePath = "")
    {
        var glob = await CallAsync("fs_glob", new Dictionary<string, object?> { ["pattern"] = pattern, ["basePath"] = basePath },
            TurnOf(person));
        return glob["entries"]?.AsArray().Select(e => e!.GetValue<string>()).ToList()
               ?? throw new InvalidOperationException(glob.ToJsonString());
    }

    public Task<JsonNode> ExecAsync(Account person, string path, string command) =>
        CallAsync("fs_exec", new Dictionary<string, object?> { ["path"] = path, ["command"] = command }, TurnOf(person));

    public async Task<string> ReadAsync(Account person, string path)
    {
        var read = await CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = path }, TurnOf(person));
        return read["content"]?.GetValue<string>() ?? throw new InvalidOperationException(read.ToJsonString());
    }

    // An app as the agent creates one: its manifest, then nothing else.
    public async Task<string> AppAsync(Account person, string slug, string name = "Glucose today")
    {
        var created = await CreateAsync(person, $"/{slug}/app.json", Manifest(name));
        return created["note"]?.GetValue<string>() ?? throw new InvalidOperationException(created.ToJsonString());
    }

    public static string Manifest(string name = "Glucose today") =>
        $$"""{"name":"{{name}}","accentColor":"#e9601f","description":"My glucose today.","reads":["/sugary"]}""";

    // What the server keeps on its volume, for the claims a caller cannot see through the mount (a
    // repository and its metadata gone).
    public IReadOnlyList<string> StoredDirectories(Account household) =>
        Directory.Exists(Path.Combine(Settings.StoragePath, household.TenantId))
            ? Directory.GetDirectories(Path.Combine(Settings.StoragePath, household.TenantId))
            : [];

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(Settings.StoragePath, recursive: true);
    }
}