using System.Security.Cryptography;
using Domain.Apps;
using Domain.DTOs.Channel;

namespace McpServerApps.Serving;

// One app session: who it is for, the accounts session it was made from (so ending that ends this),
// the one origin it opens — an app's published origin or its preview, never both — and when it ends.
public sealed record AppSession(Account Person, string SessionId, AppOrigin Origin, DateTimeOffset ExpiresAt);

// The app sessions this server holds, named to the browser by a host-only `__Host-` cookie on the
// origin each opens. Held in memory, and deliberately: the accounts service's word that a person's
// sessions ended (SessionsEnded on Redis, which ticket 04 listens for) is published once and lost to
// a server that is down, and sessions kept in memory are lost at the same moment — so no session ever
// outlives an announcement it missed. A restart costs a fresh app sign-in, which is silent while the
// person's own session lives. Indexed by person (their household is the account's tenant), because
// that is how sessions end.
public sealed class AppSessions(TimeProvider clock)
{
    public const string CookieName = "__Host-app-session";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AppSession> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<Account, HashSet<string>> _byPerson = [];

    // A new session's token: what the cookie carries, and the only way to name the session.
    public string Open(Account person, string sessionId, AppOrigin origin, DateTimeOffset expiresAt)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            DropExpired();
            _byToken[token] = new AppSession(person, sessionId, origin, expiresAt);
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