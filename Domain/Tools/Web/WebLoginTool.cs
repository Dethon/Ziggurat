using System.Text.Json.Nodes;
using Domain.DTOs.Channel;

namespace Domain.Tools.Web;

// The handoff: the person logs in themselves on a live view of a tab in their own browser
// context, and the agent inherits the session. The rules of who may start one live here; the
// tab, the link and the view are the browse server's.
public static class WebLoginTool
{
    public const string Name = "web_login";

    // What it does and what comes back; when to reach for it, and what to do with the link, is
    // the web-browsing skill's.
    public const string Description =
        """
        Opens the url in a tab of the person's own browser and answers with a one-time link to a
        live view of it, where the person logs in themselves.
        """;

    // A turn nobody sent — a schedule, a watch, the login wake itself — has nobody to open a link,
    // and a link minted there would sit in a report nobody asked for. Refused before anything is
    // opened.
    public static JsonObject? RefuseFire(ConversationContext context) => context.OriginKind is { } kind
        ? ToolError.Create(
            ToolError.Codes.PermissionDenied,
            $"This turn was started by {Raiser(kind)}, not by the person, so it cannot start a login: nobody is here to open the link.",
            "Say in your report that the person needs to log in to the site again, and that asking you directly will start the login.")
        : null;

    private static string Raiser(MessageOriginKind kind) => kind switch
    {
        MessageOriginKind.Schedule => "a schedule",
        MessageOriginKind.Watch => "a home watch",
        MessageOriginKind.Download => "a finished download",
        MessageOriginKind.Login => "a finished login",
        _ => "something other than the person"
    };
}