using System.Security.Cryptography;
using Domain.Apps;
using Domain.DTOs.Channel;
using McpServerApps.Settings;

namespace McpServerApps.Serving;

// One app session: who it is for, the one origin it opens — an app's published origin or its preview,
// never both — what proves who reads through it, when that is next renewed, and when the session ends.
public sealed record AppSession(
    Account Person, AppOrigin Origin, ViewerProof Proof, DateTimeOffset RenewAt, DateTimeOffset ExpiresAt);

// What the agent is handed with a read to believe who it is for: a signed-in viewer's `apps` assertion,
// with the accounts session it was made from (so ending that ends this), or a check's call token, the
// agent's own for the tool call that ran `./check`.
public abstract record ViewerProof
{
    public sealed record Assertion(string Token, string SessionId) : ViewerProof;

    // Never renewed: a check ends before its call token does.
    public sealed record CheckCall(string Token) : ViewerProof;
}

// The app sessions this server holds, named to the browser by a host-only `__Host-` cookie on the
// origin each opens. Held in memory, and deliberately: the accounts service's word that a person's
// sessions ended (SessionsEnded on Redis, EndedAppSessionsListener) is published once and lost to
// a server that is down, and sessions kept in memory are lost at the same moment — so no session ever
// outlives an announcement it missed. A restart costs a fresh app sign-in, which is silent while the
// person's own session lives. Indexed by person (their household is the account's tenant), because
// that is how sessions end — a check's session too, which the server opens itself for the person whose
// turn ran the check, on that call's token, and ends when the check does.
public sealed class AppSessions(TimeProvider clock, AppsSettings settings)
{
    public const string CookieName = "__Host-app-session";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AppSession> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<Account, HashSet<string>> _byPerson = [];

    // A new session's token: what the cookie carries, and the only way to name the session.
    public string Open(Account person, string sessionId, AppOrigin origin, string assertion, DateTimeOffset expiresAt) =>
        Open(new AppSession(person, origin, new ViewerProof.Assertion(assertion, sessionId),
            clock.GetUtcNow() + settings.Sessions.AssertionKept, expiresAt));

    // A check's session: its preview alone, read as the person on the call token of the call that ran it,
    // until its deadline.
    public string OpenCheck(Account person, AppOrigin origin, string callToken, DateTimeOffset expiresAt) =>
        Open(new AppSession(person, origin, new ViewerProof.CheckCall(callToken), expiresAt, expiresAt));

    private string Open(AppSession session)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var person = session.Person;
        lock (_gate)
        {
            DropExpired();
            _byToken[token] = session;
            if (!_byPerson.TryGetValue(person, out var tokens))
            {
                _byPerson[person] = tokens = new HashSet<string>(StringComparer.Ordinal);
            }

            tokens.Add(token);
        }

        return token;
    }

    // The live session a cookie names on this origin, or none: an ended, expired or unknown token,
    // or one made for another origin, opens nothing.
    public AppSession? Find(string token, AppOrigin origin)
    {
        lock (_gate)
        {
            return _byToken.TryGetValue(token, out var session) && session.Origin == origin && session.ExpiresAt > clock.GetUtcNow()
                ? session
                : null;
        }
    }

    // The session the accounts service vouched for again, with the assertion it signed.
    public void Renewed(string token, string assertion)
    {
        lock (_gate)
        {
            if (_byToken.TryGetValue(token, out var session) && session.Proof is ViewerProof.Assertion held)
            {
                _byToken[token] = session with
                {
                    Proof = held with { Token = assertion },
                    RenewAt = clock.GetUtcNow() + settings.Sessions.AssertionKept
                };
            }
        }
    }

    // One session, which the accounts service no longer vouches for.
    public void End(string token)
    {
        lock (_gate)
        {
            if (_byToken.Remove(token, out var session) && _byPerson.TryGetValue(session.Person, out var tokens)
                                                       && tokens.Remove(token) && tokens.Count == 0)
            {
                _byPerson.Remove(session.Person);
            }
        }
    }

    // Every session of this person, whatever origin it opens; answers how many ended.
    public int EndAll(Account person)
    {
        lock (_gate)
        {
            if (!_byPerson.Remove(person, out var tokens))
            {
                return 0;
            }

            return tokens.Count(_byToken.Remove);
        }
    }

    private void DropExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var (token, session) in _byToken.Where(s => s.Value.ExpiresAt <= now).ToList())
        {
            _byToken.Remove(token);
            if (_byPerson.TryGetValue(session.Person, out var tokens) && tokens.Remove(token) && tokens.Count == 0)
            {
                _byPerson.Remove(session.Person);
            }
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}