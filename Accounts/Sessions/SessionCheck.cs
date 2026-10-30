using Accounts.Pages;
using Accounts.Settings;
using Accounts.Signing;
using Domain.Security;

namespace Accounts.Sessions;

// What the proxy's forward_auth asks before every protected request. Success carries the person in
// three plain headers and a fourth that signs them, always all four, which the proxy copies onto the
// request over anything the client sent. The signed one is what an app can believe without trusting
// the network: it verifies against this service's published keys.
//
// `?channel=` names what the route guards — a channel a turn will come on (`signalr`, `sugary`) or
// an app that starts none — and the assertion names it, so one signed for Sugary cannot start a
// turn on the chat hub. It is required: a forward_auth block that forgets it is refused outright.
public static class SessionCheck
{
    public const string Path = "/verify";
    public const string UserHeader = ForwardedAccount.UserHeader;
    public const string TenantHeader = ForwardedAccount.TenantHeader;
    public const string RolesHeader = ForwardedAccount.RolesHeader;
    public const string AssertionHeader = ForwardedAccount.AssertionHeader;

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(Path, async (
            HttpContext http, SessionStore sessions, Assertions assertions, AccountsSettings settings,
            string? role, string? channel, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(channel))
            {
                return Results.BadRequest();
            }

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
            http.Response.Headers[AssertionHeader] =
                assertions.Of(identity.UserId, identity.TenantId, identity.Roles, channel);
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