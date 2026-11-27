using System.Collections.Concurrent;
using Accounts.Settings;
using Accounts.Store;

namespace Accounts.Apps;

// What an app sign-in code stands for: a signed-in session, for one built app's origin.
public sealed record AppSignInCode(string Origin, string UserId, string SessionId);

// The app sign-in's codes, between the redirect that hands one to a browser and the apps server's
// redemption of it. Kept in memory, because a code lives a redirect's worth (AppSignInCodeLifetime)
// and a restart in between costs a person nothing but a reload. Taken once, whatever the origin it is
// presented for: a code that reached another origin is spent, not left for a second try.
public sealed class AppSignInCodes(AccountsSettings settings, AccountsClock clock)
{
    private readonly ConcurrentDictionary<string, (AppSignInCode Code, DateTimeOffset Until)> _held = new(StringComparer.Ordinal);

    public string Issue(AppSignInCode code)
    {
        var now = clock.Time.GetUtcNow();
        foreach (var stale in _held.Where(h => h.Value.Until <= now).Select(h => h.Key).ToList())
        {
            _held.TryRemove(stale, out _);
        }

        var secret = Tokens.NewSecret();
        _held[secret] = (code, now + settings.AppSignInCodeLifetime);
        return secret;
    }

    // The code's session, only for the origin it was issued to and only while it lasts; spent either way.
    public AppSignInCode? Take(string secret, string origin) =>
        _held.TryRemove(secret, out var held)
        && held.Until > clock.Time.GetUtcNow()
        && string.Equals(held.Code.Origin, origin, StringComparison.OrdinalIgnoreCase)
            ? held.Code
            : null;
}