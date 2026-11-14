using Agent.Settings;
using Domain.DTOs.Channel;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;

namespace Agent.App;

// What the agent knows that a connection does not: which channels have an endpoint to dial, and
// which endpoint each one is. Everything after that — connect, register, watch, reconnect,
// re-register — is the connection's own run.
//
// An endpoint is a deployment server's URL, run once for the host's lifetime on the connection made
// for it at startup, or a household server's name: that server is not dialled but reached through
// its tunnel, and each household's tunnel of that name is a connection of that household's. The
// connection is made the first time the household opens the tunnel (householdConnection, which also
// adds it wherever the agent reads its connections) and kept: it is run once per opening and lets go
// when the opening closes, so between openings it is not connected (ADR 0011), and the next opening
// is a run of its own on the same connection.
// What a household's own channel server is told, once its household's agents have been read: the
// catalogue as each registration reads it.
public delegate Task<Func<IReadOnlyList<AgentCatalogEntry>>> HouseholdCatalogRead(ChannelHousehold household, CancellationToken ct);

public class ChannelConnectionHost(
    ChannelEndpoint[] endpoints,
    // The connections of the endpoints dialled by URL, made at startup.
    IReadOnlyList<IMcpChannelConnection> connections,
    Func<IReadOnlyList<AgentCatalogEntry>> agentCatalog,
    ILogger<ChannelConnectionHost> logger,
    // Trips once the agent's own HTTP host is listening (the host's ApplicationStarted). A channel
    // server checks the first tool call it takes against the keys the agent publishes there, and a
    // fetch that finds nobody home leaves the server refusing until it may fetch again — so nothing
    // is dialled before. A token that can never trip waits for nothing.
    CancellationToken listening = default,
    // What each deployment channel server's /mcp asks for, by the name it answers to.
    McpCredentials? credentials = null,
    // Where a household server named as a channel endpoint opens; without it such an endpoint is
    // never run.
    IOwnChannelServers? ownServers = null,
    // Completes once the home's catalogue has been read (ChannelAgentCatalogs.FirstRead), so a dialled
    // channel's first registration is the agents rather than an empty list. None waits for nothing.
    Task? catalogRead = null,
    // The catalogue a household's own channel server is told — that household's agents — once read, so
    // its first registration is its agents too. None tells it the dialled channels' catalogue.
    HouseholdCatalogRead? householdCatalog = null,
    // Makes the connection of a household's own channel server, once per household and endpoint, and
    // adds it to the agent's connections; without it such an endpoint is never run.
    Func<ChannelEndpoint, ChannelHousehold, IMcpChannelConnection>? householdConnection = null) : BackgroundService
{
    private readonly Dictionary<string, string> _endpointMap = BuildEndpointMap(endpoints);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (listening.CanBeCanceled && !listening.IsCancellationRequested)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (listening.Register(() => started.TrySetResult()))
            {
                await started.Task.WaitAsync(stoppingToken);
            }
        }

        if (catalogRead is not null)
        {
            await catalogRead.WaitAsync(stoppingToken);
        }

        var resolved = endpoints
            .Select(e => (Endpoint: e, Target: McpServerEndpoint.Of(_endpointMap[e.ChannelId], server => credentials?.For(server))))
            .ToList();
        var dialled = connections.ToDictionary(c => c.ChannelId, StringComparer.Ordinal);
        foreach (var orphan in resolved.Where(e => !e.Target.NamesOwnServer && !dialled.ContainsKey(e.Endpoint.ChannelId)))
        {
            logger.LogWarning(
                "Endpoint {ChannelId} ({Endpoint}) matches no registered channel connection and will never be run",
                orphan.Endpoint.ChannelId, orphan.Endpoint.Endpoint);
        }

        var runs = resolved
            .Select(e =>
            {
                if (e.Target.NamesOwnServer)
                {
                    return FollowAsync(e.Endpoint, e.Target.Address, stoppingToken);
                }

                if (!dialled.TryGetValue(e.Endpoint.ChannelId, out var conn))
                {
                    return Task.CompletedTask;
                }

                logger.LogInformation("Running channel {ChannelId} against {Endpoint}", conn.ChannelId, e.Target.Address);
                return conn.RunAsync(e.Target, agentCatalog, stoppingToken);
            });

        await Task.WhenAll(runs);
    }

    // Each household's tunnel of the server is followed on its own: one run at a time per household,
    // each bound to its opening. A newer opening of the same household's server replaces the old
    // (whose run is finished first, since one connection holds one client), an opening that closes
    // ends its run, and no household's opening touches another's run.
    private async Task FollowAsync(ChannelEndpoint endpoint, string serverName, CancellationToken ct)
    {
        if (ownServers is null || householdConnection is null)
        {
            logger.LogWarning(
                "Channel {ChannelId} names the household server {ServerName}, but this agent has no tunnels; it will never be run",
                endpoint.ChannelId, serverName);
            return;
        }

        logger.LogInformation(
            "Channel {ChannelId} follows every household's server {ServerName} through its tunnel",
            endpoint.ChannelId, serverName);
        var followed = new Dictionary<string, Following>(StringComparer.Ordinal);
        try
        {
            await foreach (var opening in ownServers.WatchAsync(serverName, ct))
            {
                // The old run is ended without waiting for it here: the new one starts once it has let go
                // (one connection holds one client), while every other household's opening goes on.
                var previous = followed.GetValueOrDefault(opening.HouseholdId);
                var ending = previous?.EndAsync() ?? Task.CompletedTask;
                var connection = previous?.Connection ?? householdConnection(endpoint, opening.Household);
                var current = CancellationTokenSource.CreateLinkedTokenSource(ct, opening.Closed);
                followed[opening.HouseholdId] = new Following(
                    connection, current, RunAfterAsync(ending, connection, serverName, opening, current.Token));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(followed.Values.Select(f => f.EndAsync()));
        }
    }

    private async Task RunAfterAsync(
        Task ending, IMcpChannelConnection connection, string serverName, OwnServerOpening opening, CancellationToken ct)
    {
        await ending;
        if (!ct.IsCancellationRequested)
        {
            await RunOpeningAsync(connection, serverName, opening, ct);
        }
    }

    private async Task RunOpeningAsync(
        IMcpChannelConnection connection, string serverName, OwnServerOpening opening, CancellationToken ct)
    {
        logger.LogInformation(
            "Running channel {ChannelId} through the tunnel of household {Household}'s {ServerName}",
            connection.ChannelId, opening.HouseholdId, serverName);
        var catalog = agentCatalog;
        if (householdCatalog is not null)
        {
            try
            {
                catalog = await householdCatalog(opening.Household, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        await connection.RunAsync(opening.Endpoint, catalog, ct);
        if (opening.Closed.IsCancellationRequested)
        {
            logger.LogWarning(
                "The tunnel of household {Household}'s {ServerName} closed; its channel {ChannelId} is not connected until it opens again",
                opening.HouseholdId, serverName, connection.ChannelId);
        }
    }

    private sealed record Following(IMcpChannelConnection Connection, CancellationTokenSource Current, Task Run)
    {
        private Task? _ended;

        // Once, however often asked: a replaced run is ended by its replacement and again at shutdown.
        public Task EndAsync() => _ended ??= EndOnceAsync();

        private async Task EndOnceAsync()
        {
            await Current.CancelAsync();
            await Run;
            Current.Dispose();
        }
    }

    // One endpoint per channel id, checked while the host is being built rather than once it is
    // running: a second entry for the same id is a configuration mistake, and it has to fail
    // naming the id. Letting the map throw on its own reports a duplicate key with the key
    // nowhere in the message, which leaves an operator nothing to look for.
    private static Dictionary<string, string> BuildEndpointMap(ChannelEndpoint[] endpoints)
    {
        var duplicates = endpoints
            .GroupBy(e => e.ChannelId, StringComparer.Ordinal)
            .Where(entries => entries.Count() > 1)
            .Select(entries => entries.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"ChannelEndpoints has more than one entry for channel id {string.Join(", ", duplicates)}; " +
                "each channel id must appear exactly once.");
        }

        return endpoints.ToDictionary(e => e.ChannelId, e => e.Endpoint);
    }
}