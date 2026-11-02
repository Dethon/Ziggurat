using System.Collections.Concurrent;
using Agent.App;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Infrastructure.Clients.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Unit;
using Tests.Unit.Domain;

namespace Tests.Integration.Tunnels;

// The agent's side of a household's channel server, run as the deployment runs it: channel endpoints
// naming servers, followed through the relay's openings of every household's tunnel of that name, a
// connection made per household as its tunnel first opens and added to the live set the monitor
// reads, and the monitor admitting each turn as it does. The model is the one stand-in: each turn's
// agent answers with the household and agent it was built for, so a test sees who answered.
internal sealed class TunnelledAgent : IAsyncDisposable
{
    private static readonly IReadOnlyList<AgentCatalogEntry> _catalog = [new AgentCatalogEntry("jonas", "Jonas", "the assistant")];

    private readonly CancellationTokenSource _stopping = new();
    private readonly ChannelConnectionHost _host;
    private readonly Task _monitoring;
    private ChannelAgentCatalogs? _catalogs;

    private TunnelledAgent(ChannelConnectionHost host, LiveConnectionSet<IChannelConnection> connections,
        HouseholdAnsweringFactory factory, RecordingMetricsPublisher metrics, ChatMonitor monitor)
    {
        Metrics = metrics;
        _host = host;
        Connections = connections;
        Factory = factory;
        _monitoring = monitor.Monitor(_stopping.Token);
    }

    public LiveConnectionSet<IChannelConnection> Connections { get; }

    public HouseholdAnsweringFactory Factory { get; }

    public RecordingMetricsPublisher Metrics { get; }

    public static Task<TunnelledAgent> StartAsync(RelayHost relay, params ChannelEndpoint[] endpoints) =>
        StartAsync(relay, [], endpoints);

    public static Task<TunnelledAgent> StartAsync(
        RelayHost relay, IReadOnlyList<IChannelConnection> deployment, params ChannelEndpoint[] endpoints) =>
        StartAsync(relay, deployment, new HouseholdAgentsStandIn(), endpoints);

    // With channels of the deployment's own beside the followed ones — a scheduling server's fires,
    // the chat client — standing in for the ones the agent dials by URL.
    // The households' agents are the stand-in's, read for each household's channel connections as the
    // agent reads them (ChannelAgentCatalogs), quickly enough for a test to see a change arrive.
    public static async Task<TunnelledAgent> StartAsync(
        RelayHost relay, IReadOnlyList<IChannelConnection> deployment, HouseholdAgentsStandIn agents,
        params ChannelEndpoint[] endpoints)
    {
        var connections = new LiveConnectionSet<IChannelConnection>(deployment);
        var catalogs = new ChannelAgentCatalogs(agents, TimeSpan.FromMilliseconds(200), NullLogger<ChannelAgentCatalogs>.Instance);
        await catalogs.StartAsync(CancellationToken.None);
        var host = new ChannelConnectionHost(
            endpoints, [], () => _catalog, NullLogger<ChannelConnectionHost>.Instance,
            ownServers: relay.ChannelServers,
            householdCatalog: async (household, ct) =>
            {
                await catalogs.ReadAsync(household.HouseholdId, ct);
                return () => catalogs.Current(household.HouseholdId)
                    .Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description ?? ""))
                    .ToList();
            },
            householdConnection: (endpoint, household) =>
            {
                var connection = new McpChannelConnection(
                    endpoint.ChannelId, endpoint.AttachOnly, healthCheckInterval: TimeSpan.FromMilliseconds(200),
                    callTokens: McpTestSecret.CallTokens)
                {
                    HostsReviewCopies = endpoint.HostsReviewCopies,
                    Household = household
                };
                connections.Add(connection);
                return connection;
            });
        var factory = new HouseholdAnsweringFactory();
        var metrics = new RecordingMetricsPublisher();
        var monitor = new ChatMonitor(
            connections, factory, MonitorTestMocks.CreateThreadResolver(), metrics, null,
            NullLogger<ChatMonitor>.Instance, TestAssertions.Verifier(), delegations: TestDelegations.Verifier());
        var agent = new TunnelledAgent(host, connections, factory, metrics, monitor) { _catalogs = catalogs };
        await host.StartAsync(CancellationToken.None);
        return agent;
    }

    public static ChannelEndpoint Endpoint(string serverName, bool attachOnly = false) =>
        new() { ChannelId = serverName, Endpoint = serverName, AttachOnly = attachOnly };

    // The household's connection of that channel, once its tunnel has opened.
    public IChannelConnection? ConnectionOf(string channelId, string household) =>
        Connections.FirstOrDefault(c => c.ChannelId == channelId && c.Household.HouseholdId == household);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
        if (_catalogs is not null)
        {
            await _catalogs.StopAsync(CancellationToken.None);
            _catalogs.Dispose();
        }
        try
        {
            await _monitoring;
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }
}

// Builds each turn's agent for the household it is asked for and has it say so.
internal sealed class HouseholdAnsweringFactory : IAgentFactory
{
    private readonly ConcurrentQueue<(string? TenantId, string? AgentId, Account? Account)> _created = new();

    public IReadOnlyList<(string? TenantId, string? AgentId, Account? Account)> Created => [.. _created];

    private readonly ConcurrentDictionary<(string?, string?), bool> _lacking = new();

    public static string AnswerFor(string? tenantId, string? agentId) => $"{agentId} of {tenantId ?? "nobody"} answering";

    // A household without that agent, which the real factory refuses by id.
    public void Without(string? tenantId, string agentId) => _lacking[(tenantId, agentId)] = true;

    public Task<DisposableAgent> CreateAsync(
        AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler, Account? account = null,
        string? tenantId = null, CancellationToken ct = default)
    {
        if (_lacking.ContainsKey((tenantId, agentId)))
        {
            return Task.FromException<DisposableAgent>(new AgentNotFoundException(agentId, tenantId));
        }

        _created.Enqueue((tenantId, agentId, account));
        return Task.FromResult<DisposableAgent>(new FakeAiAgent
        {
            UpdatesToYield = [new AgentResponseUpdate { Contents = [new TextContent(AnswerFor(tenantId, agentId))] }]
        });
    }

    public DisposableAgent CreateSubAgent(
        SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn) =>
        throw new NotSupportedException();
}

// Each household's agents as the accounts service would answer for them, changeable mid-test.
internal sealed class HouseholdAgentsStandIn : IHouseholdAgentSource
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AgentDefinition>> _agents = new();

    public void Set(string? householdId, params (string Id, string Name)[] agents) =>
        _agents[householdId ?? ""] = [.. agents.Select(a => new AgentDefinition
        {
            Id = a.Id, Name = a.Name, Model = "test/model", McpServerEndpoints = []
        })];

    public Task<HouseholdAgents> ForAsync(string? householdId, CancellationToken ct) =>
        Task.FromResult(new HouseholdAgents
        {
            HouseholdId = householdId,
            Agents = _agents.GetValueOrDefault(householdId ?? "", [])
        });
}