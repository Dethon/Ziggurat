using Domain.DTOs.Tunnel;
using Infrastructure.Clients.Accounts;
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
    // Beside it, every household's server of a name for the agent's key commands, which ask the
    // servers named by their server name for the delegations they keep (Home Assistant's watches):
    // which households hold one — the accounts service's word, since a server whose tunnel is closed
    // keeps its delegations and only its credential says it exists — each with whether its tunnel is
    // open, and one household's server's /mcp forwarded down its own tunnel on the mcp tag, whose pool
    // presents the tunnel credential, the command's own Authorization withheld. Another household's of
    // the same name is never reached instead.
    public static void MapConnectedServers(this IEndpointRouteBuilder app, string sharedSecret)
    {
        var group = app.MapGroup(ConnectedServers.Path).RequireSharedSecret(sharedSecret);
        group.MapGet("/{tenantId}", (string tenantId, TunnelListing listing, HttpContext http) =>
            listing.ListAsync(tenantId, http.RequestAborted));
        group.MapGet(ConnectedServers.HoldersRoute, async (
            string serverName, TunnelCredentialClient accounts, TunnelRegistry registry, CancellationToken ct) =>
        {
            IReadOnlyList<TunnelServerHolder> holders;
            try
            {
                holders = await accounts.HoldersAsync(serverName, ct);
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem(
                    $"The accounts service could not say who holds {serverName}: {ex.Message}",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(holders.Select(h => new HouseholdServer(
                h.TenantId, h.Name, serverName, h.Home, h.Suspended, registry.Find(h.TenantId, serverName) is not null, h.OwnerId)));
        });
        group.Map(ConnectedServers.McpRoute, (string tenantId, string serverName, HttpContext http, TunnelForwarder forwarder) =>
            forwarder.ForwardAsync(http, tenantId, serverName, TunnelProtocol.McpTag, "/mcp", ["Authorization"], TunnelForwarder.UnreachableAsync));
    }
}