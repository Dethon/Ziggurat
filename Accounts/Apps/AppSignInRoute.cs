using Accounts.Pages;
using Accounts.Sessions;
using Accounts.Settings;
using Domain.Apps;

namespace Accounts.Apps;

// Where a built app's origin sends a navigation that holds no app session of its own: on the main
// host, under /accounts, which the proxy never puts behind the session check. The person's own session
// is read here (a passkey sign-in first if there is none, which comes back here), and they are sent back
// to the app's origin with a code for this session, bound to that origin. A return that is not a built
// app's origin under the apps domain is refused before anything is issued (AppSignIn.ReturnTo), so a
// crafted link cannot carry a code anywhere else. Whether this person may open that app is the apps
// server's to decide; this vouches only for who they are.
public static class AppSignInRoute
{
    public const string Path = "/app-sign-in";

    public static void Map(IEndpointRouteBuilder accounts) =>
        accounts.MapGet(Path, async (
            HttpContext http, SessionStore sessions, AppSignInCodes codes, AccountsSettings settings, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var returnUrl = http.Request.Query[AppSignIn.ReturnUrlParameter].ToString();
            if (AppSignIn.ReturnTo(returnUrl, settings.AppsDomain) is not { } target)
            {
                return AccountsPages.Page(http, "Not an app of this deployment",
                    """
                    <main class="card">
                      <h1>That is not an app of this deployment</h1>
                      <p>Nothing was signed in. Open the app from its link again.</p>
                    </main>
                    """,
                    StatusCodes.Status400BadRequest);
            }

            var token = http.Request.Cookies[SessionCookie.Name];
            var session = string.IsNullOrEmpty(token) ? null : await sessions.VerifyAsync(token, ct);
            if (session is null)
            {
                return Results.Redirect(SignInPage.Url(settings,
                    $"{AccountsApp.PathBase}{Path}?{AppSignIn.ReturnUrlParameter}={Uri.EscapeDataString(returnUrl)}"));
            }

            // As every page here: nothing of a suspended household opens, an app included.
            if (session.HouseholdSuspended)
            {
                return SessionCheck.Suspended(http, page: true);
            }

            var code = codes.Issue(new AppSignInCode(target.Origin, session.UserId, session.SessionId));
            return Results.Redirect(AppSignIn.Callback(target.Origin, code, target.Path));
        });
}