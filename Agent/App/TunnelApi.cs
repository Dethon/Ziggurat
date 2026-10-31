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
    public static void MapConnectedServers(this IEndpointRouteBuilder app, string sharedSecret) =>
        app.MapGroup(ConnectedServers.Path)
            .RequireSharedSecret(sharedSecret)
            .MapGet("/{tenantId}", (string tenantId, TunnelListing listing, HttpContext http) =>
                listing.ListAsync(tenantId, http.RequestAborted));
}