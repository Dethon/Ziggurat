using Domain.DTOs.Tunnel;
using Infrastructure.Tunnels;

namespace Agent.App;

// Where a shared deployment server reaches a household's own server: the timers server ringing,
// dismissing and validating satellites on the household's voice server. On the deployment's own
// network alone — the proxy routes nothing under /internal (DeploymentConformanceTests) — and only
// for a caller presenting its own credential, to what RelayedCalls lets it reach.
public static class RelayedCallsApi
{
    public static void MapRelayedCalls(this IEndpointRouteBuilder app) =>
        app.Map($"{RelayedCallProtocol.Path}/{{server}}/{{**rest}}",
            (HttpContext http, string server, RelayedCalls calls) => calls.ForwardAsync(http, server));
}