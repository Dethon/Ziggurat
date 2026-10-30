using Accounts.Pages;
using Accounts.Settings;

namespace Accounts.Sessions;

// What the proxy's forward_auth asks before every protected request. Success carries the person in
// three headers, always all three, which the proxy copies onto the request over anything the
// client sent; an app believes them because only the proxy reaches it.
public static class SessionCheck
{
    public const string Path = "/verify";
    public const string UserHeader = "X-Ziggurat-User";
    public const string TenantHeader = "X-Ziggurat-Tenant";
    public const string RolesHeader = "X-Ziggurat-Roles";

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(Path, async (HttpContext http, SessionStore sessions, AccountsSettings settings, string? role, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var token = http.Request.Cookies[SessionCookie.Name];
            var identity = string.IsNullOrEmpty(token) ? null : await sessions.VerifyAsync(token, ct);
            if (identity is null)
            {
                var original = ForwardedRequest.From(http.Request);
                return original.IsPage
                    ? Results.Redirect(SignInPage.Url(settings, original.Url))
                    : Results.Unauthorized();
            }

            if (role is not null && !identity.Roles.Contains(role))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            http.Response.Headers[UserHeader] = identity.UserId;
            http.Response.Headers[TenantHeader] = identity.TenantId;
            http.Response.Headers[RolesHeader] = string.Join(',', identity.Roles);
            return Results.Ok();
        });
}

// The request the proxy is asking about, as forward_auth describes it in its forwarded headers.
public sealed record ForwardedRequest(string Method, string Uri, string? Url, bool AcceptsHtml)
{
    // A signed-out API or hub call is answered 401 rather than sent to a page, so the app's script
    // can tell it apart from content and take the person to sign in itself.
    private static readonly string[] _apiRoots = ["api", "hubs"];

    public bool IsPage =>
        Method == HttpMethods.Get
        && Uri.StartsWith('/')
        && !_apiRoots.Contains(Uri.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            StringComparer.OrdinalIgnoreCase)
        && AcceptsHtml;

    public static ForwardedRequest From(HttpRequest request)
    {
        var uri = request.Headers["X-Forwarded-Uri"].ToString();
        var host = request.Headers["X-Forwarded-Host"].ToString();
        var proto = request.Headers["X-Forwarded-Proto"].ToString();
        return new ForwardedRequest(
            request.Headers["X-Forwarded-Method"].ToString(),
            uri,
            // Absolute, because the page asking may be on another of the deployment's hosts than
            // the one sign-in is served on.
            host.Length > 0 && proto.Length > 0 ? $"{proto}://{host}{uri}" : uri,
            request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase));
    }
}