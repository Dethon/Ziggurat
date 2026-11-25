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
using Domain.Tools.FileSystem.Bridge;
using global::Agent.App;
using Infrastructure.Agents;
using Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Integration.Fixtures;

// The agent's app read route, hosted alone on a port of its own as the agent maps it, over households
// a test writes and servers it starts in process: what the apps server forwards a built app's reads to.
public sealed class AppReadsAgent : IAsyncDisposable
{
    // Where the deployment runs the apps server, whose credential (the suite's) is the one a read must
    // present.
    public const string AppsServer = "http://localhost:6018/mcp";

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
                .AddSingleton(McpTestSecret.Credentials)
                .AddSingleton(shared)
                .AddSingleton<IAgentDefinitionProvider>(definitions)
                .AddSingleton<IAccountStanding>(new HouseholdAccountStanding(households))
                .AddSingleton(new AgentReadRegistries(
                    definitions, McpTestSecret.Credentials,
                    new InProcessOwnServers(online ?? new Dictionary<string, string>()), McpTestSecret.CallTokens))
                .AddAppReads(new VfsBridgeSettings { MaxFileBytes = maxFileBytes }, new AppReadSettings());
            var built = builder.Build();
            built.MapAppReads();
            return built;
        });
        return new AppReadsAgent(app, port, households);
    }

    // A read as the apps server forwards it: its own credential and the viewer's `apps` assertion.
    public Task<HttpResponseMessage> ReadAsync(
        string operation, AppReadScope app, JsonObject arguments, string? assertion,
        string? credential = McpTestSecret.Value, IReadOnlyDictionary<string, string>? headers = null)
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

    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        Task.FromResult(_households[householdId]);

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