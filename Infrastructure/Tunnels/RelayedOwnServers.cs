using System.Runtime.CompilerServices;
using Infrastructure.Agents;

namespace Infrastructure.Tunnels;

// The deployment's answer to where a household's server is: its tunnel, if one is open. The endpoint
// is bound to that opening — its tools are that generation's (ADR 0012), and a call made after it
// closed fails rather than reaching whatever reopened.
public sealed class RelayedOwnServers(TunnelRegistry registry) : IOwnServers, IOwnChannelServers
{
    public McpServerEndpoint? Resolve(string? tenantId, string serverName) =>
        tenantId is null || registry.Find(tenantId, serverName) is not { IsOpen: true } tunnel
            ? null
            : McpServerEndpoint.Relayed(tunnel.Address, tunnel.Client, tunnel.Generation, tunnel.Closed);

    // A replacement's TunnelClosed arrives before its TunnelOpened, and each opening's own token says
    // when it goes, so the openings alone are what a follower needs.
    public async IAsyncEnumerable<OwnServerOpening> WatchAsync(
        string serverName, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var change in registry.WatchAsync(ct))
        {
            if (change is TunnelOpened { Tunnel: { Home: true } tunnel } && tunnel.ServerName == serverName)
            {
                yield return new OwnServerOpening(McpServerEndpoint.Relayed(tunnel.Address, tunnel.Client), tunnel.Closed);
            }
        }
    }
}