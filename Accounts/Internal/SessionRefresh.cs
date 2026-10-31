using Accounts.Sessions;
using Accounts.Signing;
using Domain.DTOs.Channel;

namespace Accounts.Internal;

// The chat hub's person for one more turn. A connection is opened on the session check's assertion,
// which lives ten minutes while the connection lives hours, so every turn the hub sends asks again —
// for the session the connection signed in with (the assertion's `sid`), named with its person, and
// answered with a fresh assertion on `signalr` only while that session lives. A session revoked from
// the account page is 404 from its next turn on, which is what stops an open chat client that was
// signed out elsewhere; nothing here answers for an account by its id alone.
public sealed record SessionQuery(string UserId, string SessionId);

public static class SessionRefresh
{
    public const string Path = "/session";

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (
            HttpContext http, SessionQuery query, SessionStore sessions, Assertions assertions, CancellationToken ct) =>
        {
            if (!InternalCallers.MayAskAbout(http, ChannelIds.SignalR))
            {
                return InternalCallers.NotYours;
            }

            return await sessions.LiveAsync(query.UserId, query.SessionId, ct) is { } session
                ? Results.Ok(new ResolvedIdentity(
                    session.TenantId, session.UserId, session.DisplayName, assertions.Of(session, ChannelIds.SignalR)))
                : Results.NotFound();
        });
}