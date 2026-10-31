using Accounts.Tunnels;
using Domain.DTOs.Tunnel;

namespace Accounts.Internal;

// What the relay asks on every tunnel's open: which household's server the credential it was
// presented opens a tunnel for, given the server name the tunnel claims. A refusal is 404 with its
// reason, so the relay can log and report why rather than guess it from a status.
public static class TunnelVerification
{
    public const string Path = "/tunnels/verify";

    public static void Map(IEndpointRouteBuilder routes) =>
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
                TunnelVerdict.Refused refused => Results.NotFound(new TunnelCredentialRefused(refused.Reason)),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        });
}