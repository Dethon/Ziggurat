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
}