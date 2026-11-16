using Infrastructure.Tunnels;

namespace Agent.App;

// Where the proxy sends /browse/* on the main host: a login handoff's live view and a person's logins
// page, which the household's web search serves at its own site. The relay forwards each exchange,
// the live view's WebSocket included, down that server's tunnel to its pages listener. Not behind
// the session check, as before: the link is the credential, so a person who only uses Telegram or
// voice opens it from their phone.
public static class BrowsePagesApi
{
    public static void MapBrowsePages(this IEndpointRouteBuilder app) =>
        app.Map($"{BrowsePages.Path}/{{**rest}}", (HttpContext http, BrowsePages pages) => pages.ForwardAsync(http));
}