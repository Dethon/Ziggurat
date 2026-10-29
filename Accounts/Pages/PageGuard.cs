using Accounts.Sessions;
using Accounts.Settings;
using Microsoft.AspNetCore.Antiforgery;

namespace Accounts.Pages;

// Who a request to the account or members page is from. The proxy does not put /accounts/* behind
// the session check, since sign-in and enrollment must open signed out, so these pages read the
// session cookie themselves, as the check does.
//
// Every post also carries the antiforgery token its page rendered. The session cookie is Lax, so a
// cross-site form arrives without it, but every host under the deployment's parent domain is the
// same site, and these forms decide who gets in.
public sealed class PageGuard(SessionStore sessions, IAntiforgery antiforgery, AccountsSettings settings)
{
    public async Task<(SessionIdentity? Viewer, IResult? Refusal)> CheckAsync(
        HttpContext http, string returnTo, bool ownersOnly, CancellationToken ct)
    {
        var token = http.Request.Cookies[SessionCookie.Name];
        var viewer = string.IsNullOrEmpty(token) ? null : await sessions.VerifyAsync(token, ct);
        if (viewer is null)
        {
            return (null, http.Request.Path.StartsWithSegments($"{AccountsApp.PathBase}/api")
                ? Results.Unauthorized()
                : Results.Redirect(SignInPage.Url(settings, returnTo)));
        }

        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return (null, AccountsPages.Page(http, "Try again",
                """
                <main>
                  <h1>That form had gone stale.</h1>
                  <p>Go back, reload the page and try again.</p>
                </main>
                """,
                StatusCodes.Status400BadRequest));
        }

        if (ownersOnly && !viewer.Roles.Contains(Roles.Owner))
        {
            return (null, AccountsPages.Page(http, "Owners only",
                $"""
                 <main>
                   <h1>Only an owner manages the household.</h1>
                   <p><a href="{AccountPage.Url}">Back to your account</a></p>
                 </main>
                 """,
                StatusCodes.Status403Forbidden));
        }

        return (viewer, null);
    }

    // The hidden field a page's forms post back, and the cookie it is checked against.
    public string FormField(HttpContext http)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        return $"""<input type="hidden" name="{tokens.FormFieldName}" value="{AccountsPages.Encode(tokens.RequestToken ?? "")}">""";
    }

    // The same token for the page's script, which sends it as a header.
    public string RequestToken(HttpContext http) => antiforgery.GetAndStoreTokens(http).RequestToken ?? "";
}