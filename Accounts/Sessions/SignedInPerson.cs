namespace Accounts.Sessions;

// Who is signed in, as an app's page shows them: asked same-origin through the proxy with the
// session cookie, so the chat client takes its person's name and avatar from the account rather
// than from anything the browser kept. Not behind the session check: it is the check, for a page.
public sealed record SignedInPerson(
    string UserId, string TenantId, string DisplayName, string? AvatarUrl, IReadOnlyList<string> Roles)
{
    public const string Path = "/me";

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(Path, async (HttpContext http, SessionStore sessions, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var token = http.Request.Cookies[SessionCookie.Name];

            // Answered plainly, never redirected: the asker is a script, which sends the person to
            // sign in itself.
            return (string.IsNullOrEmpty(token) ? null : await sessions.VerifyAsync(token, ct)) is { } identity
                ? Results.Ok(new SignedInPerson(
                    identity.UserId, identity.TenantId, identity.DisplayName, identity.AvatarUrl, identity.Roles))
                : Results.Unauthorized();
        });
}