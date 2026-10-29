using Accounts.Settings;

namespace Accounts.Sessions;

public static class SessionCookie
{
    public const string Name = "ziggurat_session";

    // A session lasts until it is revoked, and a browser keeps a cookie at most 400 days, so that is
    // what it is asked for. Lax rather than Strict: a link to one of the apps, followed from a chat
    // or a mail, has to arrive signed in.
    private static readonly TimeSpan _browserLimit = TimeSpan.FromDays(400);

    public static void Write(HttpResponse response, AccountsSettings settings, string token) =>
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Domain = settings.SessionCookie.Domain.Length > 0 ? settings.SessionCookie.Domain : null,
            MaxAge = _browserLimit,
            IsEssential = true
        });
}