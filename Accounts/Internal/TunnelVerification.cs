using Accounts.Tunnels;
using Domain.DTOs.Tunnel;

namespace Accounts.Internal;

// What the relay asks on every tunnel's open: which household's server the credential it was
// presented opens a tunnel for, given the server name the tunnel claims. A refusal is 404 with its
// reason, so the relay can log and report why rather than guess it from a status.
public static class TunnelVerification
{
    public const string Path = "/tunnels/verify";

    // Beside it, what the agent's key commands ask through the agent: which households hold a server
    // of a name, its tunnel open or not. The relay's question, so the relay's caller alone.
    public const string HoldersPath = "/tunnels/holders";

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost(HoldersPath, async (
            HttpContext http, TunnelServerHoldersQuery query, TunnelCredentialStore credentials, CancellationToken ct) =>
            InternalCallers.MayAskAbout(http, InternalCallers.Tunnels)
                ? Results.Ok(await credentials.HoldersAsync(query.ServerName, ct))
                : InternalCallers.NotYours);
        MapVerify(routes);
    }

    private static void MapVerify(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (
            HttpContext http, TunnelCredentialQuery query, TunnelCredentialStore credentials, CancellationToken ct) =>
        {
            if (!InternalCallers.MayAskAbout(http, InternalCallers.Tunnels))
            {
                return InternalCallers.NotYours;
            }

            return await credentials.VerifyAsync(query.Credential, query.ServerName, ct) switch
            {
                TunnelVerdict.Opens opens => Results.Ok(opens.Credential),
                TunnelVerdict.Refused refused => Results.NotFound(new TunnelCredentialRefused(refused.Reason, refused.TenantId)),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        });
}