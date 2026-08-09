using Agent.Settings;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;

namespace Agent.App;

// What the agent knows that a connection does not: which channels have an endpoint to dial, and
// which endpoint each one is. Everything after that — connect, register, watch, reconnect,
// re-register — is the connection's own run.
public class ChannelConnectionHost(
    ChannelEndpoint[] endpoints,
    IReadOnlyList<IMcpChannelConnection> connections,
    Func<IReadOnlyList<AgentCatalogEntry>> agentCatalog,
    ILogger<ChannelConnectionHost> logger) : BackgroundService
{
    private readonly Dictionary<string, string> _endpointMap = BuildEndpointMap(endpoints);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                logger.LogInformation("Running channel {ChannelId} against {Endpoint}", conn.ChannelId, endpoint);
                return conn.RunAsync(endpoint, agentCatalog, stoppingToken);
            });

        await Task.WhenAll(runs);
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