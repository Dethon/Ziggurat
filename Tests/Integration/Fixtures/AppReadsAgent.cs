using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Domain.Tools.FileSystem.Bridge;
using global::Agent.App;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using Mcp.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Tests.Integration.McpServers;

namespace Tests.Integration.Fixtures;

// The agent's app read route, hosted alone on a port of its own as the agent maps it, over households
// a test writes and servers it starts in process: what the apps server forwards a built app's reads to.
public sealed class AppReadsAgent : IAsyncDisposable
{
    // Where the deployment runs the apps server, whose credential (the suite's) is the one a read must
    // present.
    public const string AppsServer = TestCheckCalls.AppsServer;

    private readonly WebApplication _app;
    private readonly HttpClient _client = new();

    private AppReadsAgent(WebApplication app, int port, AgentHouseholds households)
    {
        _app = app;
        Address = $"http://localhost:{port}";
        Households = households;
    }

    public string Address { get; }

    public AgentHouseholds Households { get; }

    public ReadRegistryCache Registries => _app.Services.GetRequiredService<ReadRegistryCache>();

    public static async Task<AppReadsAgent> StartAsync(
        AgentHouseholds households,
        SharedServers? shared = null,
        IReadOnlyDictionary<string, string>? online = null,
        long maxFileBytes = 64 * 1024 * 1024)
    {
        shared = (shared ?? new SharedServers()) with { Apps = AppsServer };
        var definitions = new AgentDefinitionProvider(new HouseholdAgentSource(households, shared), new CustomAgentRegistry());
        var (port, app) = await TestPort.StartWebAsync(port =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services
                .AddSingleton(TestAssertions.Verifier())
                .AddSingleton(TestCheckCalls.Verifier())
                .AddSingleton(McpTestSecret.Credentials)
                .AddSingleton(shared)
                .AddSingleton<IAgentDefinitionProvider>(definitions)
                .AddSingleton<IAccountStanding>(new HouseholdAccountStanding(households))
                .AddSingleton(new AgentReadRegistries(
                    definitions, McpTestSecret.Credentials, TimeProvider.System,
                    new InProcessOwnServers(online ?? new Dictionary<string, string>()), McpTestSecret.CallTokens))
                .AddAppReads(new VfsBridgeSettings { MaxFileBytes = maxFileBytes }, new AppReadSettings());
            var built = builder.Build();
            built.MapAppReads();
            return built;
        });
        return new AppReadsAgent(app, port, households);
    }

    // A read as the apps server forwards it: its own credential and the viewer's `apps` assertion, or a
    // check's call token in its place.
    public Task<HttpResponseMessage> ReadAsync(
        string operation, AppReadScope app, JsonObject arguments, string? assertion,
        string? credential = McpTestSecret.Value, IReadOnlyDictionary<string, string>? headers = null, string? callToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Address + AppReads.AgentRoute(operation))
        {
            Content = JsonContent.Create(new AppReadRequest { App = app, Arguments = arguments })
        };
        if (credential is not null)
        {
            request.Headers.Add("Authorization", SharedSecret.Header(credential));
        }

        if (assertion is not null)
        {
            request.Headers.Add(ForwardedAccount.AssertionHeader, assertion);
        }

        if (callToken is not null)
        {
            request.Headers.Add(AppReads.CheckCallTokenHeader, callToken);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        return _client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

// A household's text filesystem, published as `/<name>` by a server of its own, its files on a disk the
// test writes: text read, search, glob, info and the blob read a copy streams through.
public sealed class TextFileServer : IAsyncDisposable
{
    private readonly RunningServer _server;

    private TextFileServer(RunningServer server, string directory)
    {
        _server = server;
        Directory = directory;
    }

    public string Directory { get; }

    public string Endpoint => _server.Endpoint;

    public static async Task<TextFileServer> StartAsync(string name, IReadOnlyDictionary<string, string> files)
    {
        var directory = Path.Combine(Path.GetTempPath(), "app-reads-tests", Guid.NewGuid().ToString("N"));
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(directory, path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
        }

        System.IO.Directory.CreateDirectory(directory);
        var server = await InMemoryMcpServer.StartAsync(services => services
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(sp => new TextDiskFileSystem(
                name, $"The household's {name}.", sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(directory), [".md", ".txt", ".json", ".png"]))
            .AddToolServer(new TextServerSettings(), ToolResponse.Create)
            .AddFileSystemTools<TextDiskFileSystem>()
            .AddFileSystemResource<TextDiskFileSystem>());
        return new TextFileServer(server, directory);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed record TextServerSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }
}

// The households' configuration as the accounts service would answer the agent, each written by the test.
public sealed class AgentHouseholds : IHouseholdConfiguration
{
    private readonly Dictionary<string, AgentHouseholdConfig> _households = [];

    public void Add(
        string id, params AgentDefinition[] agents) => Add(id, agents, null);

    public void Add(
        string id, IReadOnlyList<AgentDefinition> agents, FeaturesSection? features,
        IReadOnlyList<string>? ownServers = null, bool suspended = false, IReadOnlyList<string>? disabled = null) =>
        _households[id] = new AgentHouseholdConfig(
            id, false, new HouseholdSettings { Name = "Casa", TimeZone = "UTC" },
            new AgentsSection { Agents = [.. agents.Select(d => new HouseholdAgent { Id = d.Id, Definition = d })] },
            features ?? new FeaturesSection(), new SpacesSection(), "household-key",
            DisabledPeople: disabled, Suspended: suspended, OwnServers: ownServers);

    public void Change(string id, Func<AgentHouseholdConfig, AgentHouseholdConfig> change) =>
        _households[id] = change(_households[id]);

    // The accounts service not answering: every household's configuration is an outage.
    public bool Unreadable { get; set; }

    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        Unreadable
            ? Task.FromException<AgentHouseholdConfig>(new HttpRequestException("the accounts service is down"))
            : Task.FromResult(_households[householdId]);

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct) =>
        throw new NotSupportedException();
}