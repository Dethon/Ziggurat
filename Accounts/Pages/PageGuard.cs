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
        HttpContext http, string returnTo, bool ownersOnly, CancellationToken ct, bool refuseSuspended = true)
    {
        var token = http.Request.Cookies[SessionCookie.Name];
        var viewer = string.IsNullOrEmpty(token) ? null : await sessions.VerifyAsync(token, ct);
        var script = http.Request.Path.StartsWithSegments($"{AccountsApp.PathBase}/api");
        if (viewer is null)
        {
            return (null, script ? Results.Unauthorized() : Results.Redirect(SignInPage.Url(settings, returnTo)));
        }

        // As the session check refuses it, and first, so a stale form is not what they are told: nothing
        // of a suspended household opens until it is resumed, and an owner's invite or recovery link
        // minted here would be a way in that outlasts the suspension.
        if (refuseSuspended && viewer.HouseholdSuspended)
        {
            return (null, SessionCheck.Suspended(http, page: !script, GoesBackTo(http.Request.Query["returnUrl"])));
        }

        if (!await antiforgery.IsRequestValidAsync(http))
        {
            // Nothing on an installed app goes back, so the page itself is the way to a fresh form.
            return (null, AccountsPages.Page(http, "Try again",
                $"""
                 <main class="card">
                   <h1>That form had gone stale</h1>
                   <p>Nothing was changed. Open the page again and try once more.</p>
                   <a class="button primary" href="{AccountsPages.Encode(returnTo)}">Open it again</a>
                 </main>
                 """,
                StatusCodes.Status400BadRequest));
        }

        if (ownersOnly && !viewer.Roles.Contains(Roles.Owner))
        {
            return (null, AccountsPages.Page(http, "Owners only",
                $"""
                 <main class="card">
                   <h1>Only an owner manages the household</h1>
                   <a class="button primary" href="{AccountPage.Url}">Back to your account</a>
                 </main>
                 """,
                StatusCodes.Status403Forbidden));
        }

        return (viewer, null);
    }

    // Where a suspended household's notice goes once it is resumed: the page that sent it here, when that
    // is one of the deployment's; nowhere named otherwise, and the notice loads itself again.
    private string? GoesBackTo(string? returnUrl) => ReturnUrls.Allowed(settings, returnUrl);

    // The hidden field a page's forms post back, and the cookie it is checked against.
    public string FormField(HttpContext http)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        return $"""<input type="hidden" name="{tokens.FormFieldName}" value="{AccountsPages.Encode(tokens.RequestToken ?? "")}">""";
    }

    // The same token for the page's script, which sends it as a header.
    public string RequestToken(HttpContext http) => antiforgery.GetAndStoreTokens(http).RequestToken ?? "";
}