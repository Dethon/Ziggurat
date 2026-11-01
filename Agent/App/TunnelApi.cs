using Domain.DTOs.Tunnel;
using Infrastructure.Tunnels;

namespace Agent.App;

// Where a household's server opens its tunnel: the proxy's one route for it on the main host
// (DeploymentConformanceTests), a WebSocket the relay admits by the credential it presents. Anything
// that is not a WebSocket upgrade is answered 400.
public static class TunnelApi
{
    public static void MapTunnel(this IEndpointRouteBuilder app) =>
        app.Map(TunnelProtocol.Path, (HttpContext http, TunnelRelay relay) => relay.AcceptAsync(http));

    // A household's connected servers, for the operator command run beside the agent. Under /internal,
    // which the proxy never routes, and behind the agent API's secret, which the command reads from the
    // same environment: what tools a household's machines offer is nobody else's to list.
    //
    // Beside it, the home household's servers for the agent's key commands, which ask the servers
    // named by their server name for the delegations they keep (Home Assistant's watches): which
    // household is the home's, and the server's /mcp forwarded down its tunnel on the mcp tag — whose
    // pool presents the tunnel credential, the command's own Authorization withheld. The home's alone:
    // the commands name channel endpoints, which are the home's (.claude/rules/tunnel.md).
    public static void MapConnectedServers(this IEndpointRouteBuilder app, string sharedSecret)
    {
        var group = app.MapGroup(ConnectedServers.Path).RequireSharedSecret(sharedSecret);
        group.MapGet("/{tenantId}", (string tenantId, TunnelListing listing, HttpContext http) =>
            listing.ListAsync(tenantId, http.RequestAborted));
        group.MapGet(ConnectedServers.HomeRoute, (string serverName, TunnelRegistry registry) =>
            registry.FindHome(serverName) is { } open
                ? Results.Ok(new HomeServer(open.TenantId, open.ServerName))
                : Results.NotFound());
        group.Map(ConnectedServers.HomeMcpRoute, (string serverName, HttpContext http, TunnelForwarder forwarder) =>
            forwarder.ForwardHomeAsync(http, serverName, TunnelProtocol.McpTag, "/mcp", ["Authorization"], TunnelForwarder.UnreachableAsync));
    }
}