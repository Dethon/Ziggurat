using System.Net;
using Domain.DTOs.Tunnel;
using Microsoft.AspNetCore.Http;

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

    public Task ForwardAsync(HttpContext context) =>
        BrowsePagePath.HouseholdOf(context.Request.Path) is { } household
            ? forwarder.ForwardAsync(
                context, household, HouseholdServerNames.WebSearch, TunnelProtocol.PagesTag, context.Request.Path, [],
                OfflineAsync)
            : forwarder.ForwardHomeAsync(
                context, HouseholdServerNames.WebSearch, TunnelProtocol.PagesTag, context.Request.Path, [], OfflineAsync);

    private static async Task OfflineAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "text/html; charset=utf-8";
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