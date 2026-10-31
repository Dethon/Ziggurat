using Infrastructure.Agents;

namespace Infrastructure.Tunnels;

// The deployment's answer to where a household's server is: its tunnel, if one is open. The endpoint
// is bound to that opening — its tools are that generation's (ADR 0012), and a call made after it
// closed fails rather than reaching whatever reopened.
public sealed class RelayedOwnServers(TunnelRegistry registry) : IOwnServers
{
    public McpServerEndpoint? Resolve(string? tenantId, string serverName) =>
        tenantId is null || registry.Find(tenantId, serverName) is not { IsOpen: true } tunnel
            ? null
            : McpServerEndpoint.Relayed(tunnel.Address, tunnel.Client, tunnel.Generation, tunnel.Closed);
}