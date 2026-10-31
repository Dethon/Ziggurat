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
// An endpoint is a deployment server's URL, run once for the host's lifetime, or a household server's
// name: that server is not dialled but reached through its tunnel, so its connection is run once per
// opening of the tunnel and lets go when the opening closes, and the next opening is a run of its own.
public class ChannelConnectionHost(
    ChannelEndpoint[] endpoints,
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
    IOwnChannelServers? ownServers = null) : BackgroundService
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

        var endpointMap = _endpointMap;

        var connectionIds = connections.Select(c => c.ChannelId).ToHashSet();
        foreach (var orphan in endpoints.Where(e => !connectionIds.Contains(e.ChannelId)))
        {
            logger.LogWarning(
                "Endpoint {ChannelId} ({Endpoint}) matches no registered channel connection and will never be run",
                orphan.ChannelId, orphan.Endpoint);
        }

        var runs = connections
            .Where(c => endpointMap.ContainsKey(c.ChannelId))
            .Select(conn =>
            {
                var endpoint = endpointMap[conn.ChannelId];
                if (TunnelCredentialProtocol.IsServerName(endpoint))
                {
                    return FollowAsync(conn, endpoint, stoppingToken);
                }

                logger.LogInformation("Running channel {ChannelId} against {Endpoint}", conn.ChannelId, endpoint);
                return conn.RunAsync(
                    McpServerEndpoint.Configured(
                        endpoint, ServerAddress.FromUrl(endpoint) is { } server ? credentials?.For(server) : null), agentCatalog, stoppingToken);
            });

        await Task.WhenAll(runs);
    }

    // One run at a time, each bound to its opening: a newer opening of the same server replaces the
    // old (whose run is finished first, since one connection holds one client), and an opening that
    // closes ends its run, leaving the channel not connected until the server opens its tunnel again.
    private async Task FollowAsync(IMcpChannelConnection connection, string serverName, CancellationToken ct)
    {
        if (ownServers is null)
        {
            logger.LogWarning(
                "Channel {ChannelId} names the household server {ServerName}, but this agent has no tunnels; it will never be run",
                connection.ChannelId, serverName);
            return;
        }

        logger.LogInformation(
            "Channel {ChannelId} follows the home household's server {ServerName} through its tunnel",
            connection.ChannelId, serverName);
        CancellationTokenSource? current = null;
        var run = Task.CompletedTask;
        try
        {
            await foreach (var opening in ownServers.WatchAsync(serverName, ct))
            {
                if (current is not null)
                {
                    await current.CancelAsync();
                    await run;
                    current.Dispose();
                }

                current = CancellationTokenSource.CreateLinkedTokenSource(ct, opening.Closed);
                run = RunOpeningAsync(connection, serverName, opening, current.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            current?.Cancel();
            await run;
            current?.Dispose();
        }
    }

    private async Task RunOpeningAsync(
        IMcpChannelConnection connection, string serverName, OwnServerOpening opening, CancellationToken ct)
    {
        logger.LogInformation(
            "Running channel {ChannelId} through the tunnel of {ServerName}", connection.ChannelId, serverName);
        await connection.RunAsync(opening.Endpoint, agentCatalog, ct);
        if (opening.Closed.IsCancellationRequested)
        {
            logger.LogWarning(
                "The tunnel of {ServerName} closed; channel {ChannelId} is not connected until it opens again",
                serverName, connection.ChannelId);
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