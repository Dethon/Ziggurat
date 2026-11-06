using System.Net;
using Domain.DTOs.Tunnel;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Infrastructure.Tunnels;

// The browse pages a person opens from a link, answered by the web search of the household the link
// names (BrowsePagePath) at its own site: every exchange under /browse/<household> goes down that
// household's server's tunnel to its pages listener as it came, so the page's own rules — one claim
// per link, the cookie that binds the view, the origin check on the live view's socket — are the
// server's and unchanged. With that server offline, the person is told so on a page of the relay's
// own; another household's web search is never asked instead, the home's included.
//
// A link naming no household was minted by a web search from before links named one, when every link
// was the home's: it still goes to the home's, so a kit not yet rebuilt keeps its links working.
public sealed class BrowsePages(TunnelForwarder forwarder)
{
    public const string Path = BrowsePagePath.Root;

    // Headers of the server's that the policy answers for instead, or that would reach past its pages:
    // Clear-Site-Data wipes the whole origin's cookies, the chat client's session among them.
    private static readonly string[] _dropped =
    [
        "Content-Security-Policy", "Content-Security-Policy-Report-Only", "Clear-Site-Data", "Service-Worker-Allowed",
        "X-Content-Type-Options"
    ];

    public Task ForwardAsync(HttpContext context)
    {
        Withhold(context.Request);
        return BrowsePagePath.HouseholdOf(context.Request.Path) is { } household
            ? forwarder.ForwardAsync(
                context, household, HouseholdServerNames.WebSearch, TunnelProtocol.PagesTag, context.Request.Path, [],
                OfflineAsync, answer => Confine(answer, BrowsePagePath.For(household)))
            : forwarder.ForwardHomeAsync(
                context, HouseholdServerNames.WebSearch, TunnelProtocol.PagesTag, context.Request.Path, [], OfflineAsync,
                answer => Confine(answer, BrowsePagePath.Root));
    }

    // The browser sends every cookie of the origin on Path=/ with a link's request, the person's
    // session among them, and the server it goes to may be a household's own machine: it is sent the
    // page's view cookies alone, so whoever runs it never holds the session of whoever opened the link.
    private static void Withhold(HttpRequest request)
    {
        request.Headers.Remove(HeaderNames.Authorization);
        var kept = request.Headers.Cookie
            .SelectMany(header => (header ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(pair => BrowsePagePolicy.ViewCookies.Contains(pair.Split('=', 2)[0]))
            .ToArray();
        request.Headers.Remove(HeaderNames.Cookie);
        if (kept.Length > 0)
        {
            request.Headers.Cookie = string.Join("; ", kept);
        }
    }

    // The answer of a server the deployment does not run, on the deployment's own origin: it runs the
    // pinned scripts alone (BrowsePagePolicy), and keeps a cookie only when it is one of the page's view
    // cookies on exactly its own pages, for this host alone.
    private static void Confine(HttpContext context, string pages)
    {
        var headers = context.Response.Headers;
        foreach (var name in _dropped)
        {
            headers.Remove(name);
        }

        var kept = headers.SetCookie.Where(cookie => cookie is not null && IsAViewCookieOf(cookie, pages)).ToArray();
        headers.Remove(HeaderNames.SetCookie);
        if (kept.Length > 0)
        {
            headers.SetCookie = kept;
        }

        headers.ContentSecurityPolicy = BrowsePagePolicy.ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
    }

    private static bool IsAViewCookieOf(string cookie, string pages)
    {
        var parts = cookie.Split(';', StringSplitOptions.TrimEntries);
        var name = parts[0].Split('=', 2)[0];
        var attributes = parts.Skip(1).Select(a => a.Split('=', 2)).ToList();
        return BrowsePagePolicy.ViewCookies.Contains(name)
               && attributes.Count(a => a[0].Equals("path", StringComparison.OrdinalIgnoreCase)) == 1
               && attributes.Any(a => a[0].Equals("path", StringComparison.OrdinalIgnoreCase) && a.Length == 2 && a[1] == pages)
               && !attributes.Any(a => a[0].Equals("domain", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task OfflineAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.ContentSecurityPolicy = BrowsePagePolicy.ContentSecurityPolicy;
        await context.Response.WriteAsync(OfflinePage(reason), context.RequestAborted);
    }

    // The web search server's palette (HandoffPage), so the answer looks like the page it stands in for.
    private static string OfflinePage(string reason) =>
        $$"""
         <!doctype html>
         <html lang="en"><head><meta charset="utf-8">
         <meta name="viewport" content="width=device-width, initial-scale=1">
         <title>Browser offline</title>
         <style>
         :root { --bg:#f3ede1; --ink:#2a2118; --muted:#7a6a52; color-scheme: light dark; }
         @media (prefers-color-scheme: dark) { :root { --bg:#171310; --ink:#f1e7d6; --muted:#b09d80; } }
         body { margin:0; background:var(--bg); color:var(--ink); font: 15px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
         main { max-width: 640px; margin: 0 auto; padding: 32px 16px; }
         h1 { font-size: 17px; font-weight: 600; margin: 4px 0 2px; }
         p { color: var(--muted); }
         </style></head>
         <body><main>
         <h1>Your household's browser is offline</h1>
         <p>This page is served by the browser at your household's own site, and it is not connected right now. Check that it is running, then open the link again.</p>
         <p><small>{{WebUtility.HtmlEncode(reason)}}</small></p>
         </main></body></html>
         """;
}