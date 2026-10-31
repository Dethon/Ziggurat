using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Outposts;
using Infrastructure.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Tunnels;

// The deployment's outposts: the household's open tunnels whose verified credential was minted for
// `outpost-<NAME>` (OutpostServerName). A machine is there while its tunnel is open and gone when it
// closes — its opening is what registering was, the tunnel's keep-alive is what the keepalive was,
// and nothing here expires or sweeps.
//
// A session asks for the snapshot at the moment it is built, so a machine whose tunnel opened a
// moment ago is in the next session. The watch records each opening and closing as an OutpostEvent,
// because an outpost leaves no other trace of having been there.
public sealed class TunnelledOutposts(
    TunnelRegistry registry,
    IMetricsPublisher metrics,
    TimeProvider clock,
    ILogger<TunnelledOutposts>? logger = null) : BackgroundService, IOutposts
{
    public IReadOnlyList<LiveOutpost> Live(string? tenantId) =>
        tenantId is null
            ? []
            :
            [
                .. registry.Open
                    .Where(tunnel => tunnel.TenantId == tenantId && tunnel.IsOpen)
                    .Select(tunnel => (Tunnel: tunnel, Name: OutpostServerName.NameOf(tunnel.ServerName)))
                    .Where(outpost => outpost.Name is not null)
                    .OrderBy(outpost => outpost.Name, StringComparer.Ordinal)
                    .Select(outpost => new LiveOutpost(
                        outpost.Name!, McpServerEndpoint.RelayedOutpost(
                            outpost.Tunnel.Address, outpost.Tunnel.Client, outpost.Tunnel.Generation, outpost.Tunnel.Closed)))
            ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in registry.WatchAsync(stoppingToken))
            {
                if (OutpostServerName.NameOf(change.Tunnel.ServerName) is { } name)
                {
                    Record(change, name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Record(TunnelChange change, string name)
    {
        var (lifecycle, reason) = change is TunnelClosed closed
            ? (OutpostLifecycle.Closed, closed.Reason)
            : (OutpostLifecycle.Opened, (string?)null);
        logger?.LogInformation(
            "Outpost {Name} of household {TenantId} {Lifecycle}{Reason}",
            name, change.Tunnel.TenantId, lifecycle, reason is null ? "" : $": {reason}");
        metrics.Publish(new OutpostEvent
        {
            Timestamp = clock.GetUtcNow(),
            Outpost = name,
            TenantId = change.Tunnel.TenantId,
            Lifecycle = lifecycle,
            Reason = reason
        });
    }
}