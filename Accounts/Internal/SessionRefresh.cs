using Accounts.Sessions;
using Accounts.Signing;
using Domain.DTOs.Channel;
using Domain.Security;

namespace Accounts.Internal;

// The chat hub's person for one more turn. A connection is opened on the session check's assertion,
// which lives ten minutes while the connection lives hours, so every turn the hub sends asks again —
// for the session the connection signed in with (the assertion's `sid`), named with its person, and
// answered with a fresh assertion on `signalr` only while that session lives. A session revoked from
// the account page is 404 from its next turn on, which is what stops an open chat client that was
// signed out elsewhere; nothing here answers for an account by its id alone.
//
// Channel: the one the caller asks for, which it must be allowed. A caller that names none is answered
// on the first of its own (signalr, sugary, apps); a host run hands every server one credential, which
// is then each of those callers at once, so the apps server names `apps` rather than be handed the hub's.
public sealed record SessionQuery(string UserId, string SessionId, string? Channel = null);

public static class SessionRefresh
{
    public const string Path = "/session";

    // The channels whose people are kept by their session, in the order an unnamed ask is answered on.
    // persons-apps is the hub's ask for the apps server's listing of a person's apps (WebChat's space
    // switcher), always named, after signalr so an unnamed ask of the hub's stays on its own channel.
    private static readonly string[] _refreshed = [ChannelIds.SignalR, ChannelIds.Sugary, AppChannels.Apps, AppChannels.PersonsApps];

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (
            HttpContext http, SessionQuery query, SessionStore sessions, Assertions assertions, CancellationToken ct) =>
        {
            // The chat hub's (on its own channel, or for its space switcher on persons-apps), Sugary's for a
            // phone a session subscribed, or the apps server's for an app session: each answered on its own channel.
            var channel = query.Channel is { } named
                ? _refreshed.Contains(named) && InternalCallers.MayAskAbout(http, named) ? named : null
                : _refreshed.FirstOrDefault(c => InternalCallers.MayAskAbout(http, c));
            if (channel is null)
            {
                return InternalCallers.NotYours;
            }

            return await sessions.LiveAsync(query.UserId, query.SessionId, ct) is { } session
                ? Results.Ok(new ResolvedIdentity(
                    session.TenantId, session.UserId, session.DisplayName, assertions.Of(session, channel),
                    session.HouseholdSuspended))
                : Results.NotFound();
        });
}