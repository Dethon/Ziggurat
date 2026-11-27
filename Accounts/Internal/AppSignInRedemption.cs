using Accounts.Apps;
using Accounts.Sessions;
using Accounts.Signing;
using Domain.DTOs.Channel;
using Domain.Security;

namespace Accounts.Internal;

// The apps server's half of the app sign-in: the code a browser brought back to a built app's
// origin, with that origin, for the person whose session it was issued for — an assertion on `apps`
// naming the session (`sid`), which the apps server then renews by the session refresh. 404 for a code
// that is unknown, spent, run out or issued for another origin, and for a session ended meanwhile.
public sealed record AppSignInRedemptionQuery(string Code, string Origin);

public static class AppSignInRedemption
{
    public const string Path = "/app-sign-in";

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (
            HttpContext http, AppSignInRedemptionQuery query, AppSignInCodes codes, SessionStore sessions, Assertions assertions,
            CancellationToken ct) =>
        {
            // Before the code is taken, so another caller's try spends nothing.
            if (!InternalCallers.MayAskAbout(http, AppChannels.Apps))
            {
                return InternalCallers.NotYours;
            }

            return codes.Take(query.Code, query.Origin) is { } code
                   && await sessions.LiveAsync(code.UserId, code.SessionId, ct) is { HouseholdSuspended: false } session
                ? Results.Ok(new ResolvedIdentity(
                    session.TenantId, session.UserId, session.DisplayName, assertions.Of(session, AppChannels.Apps)))
                : Results.NotFound();
        });
}