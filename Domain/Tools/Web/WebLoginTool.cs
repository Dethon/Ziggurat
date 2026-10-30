using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Domain.Tools.Web;

// The handoff: the person logs in themselves on a live view of a tab in their own browser
// context, and the agent inherits the session. Who may start one is decided here; the tab, the
// link and the view are the browse server's.
public class WebLoginTool(ILoginHandoffs handoffs)
{
    public const string Name = "web_login";

    // What it does and what comes back; when to reach for it, and what to do with the link, is
    // the web-browsing skill's.
    protected const string Description =
        """
        Opens the url in a tab of the person's own browser and answers with a one-time link to a
        live view of it, where the person logs in themselves.
        """;

    protected async Task<JsonObject> RunAsync(ConversationContext? context, string url, string? delegation, CancellationToken ct)
    {
        var resolution = LoginLinks.Resolve(context, handoffs.IsConfigured);
        if (resolution is PersonResolution.Refused refused)
        {
            return refused.Error;
        }

        if (!LoginLinks.IsWebUrl(url, out var uri))
        {
            return ToolError.Create(ToolError.Codes.InvalidArgument, "Only an http or https url can be handed off.");
        }

        // A voice turn cannot show a link, so its handoff is put in a conversation of the person's
        // chat client instead: the account the turn acts for is who that conversation is for, and
        // only their own sign-in opens the chat client as them.
        var voice = context!.Origin.ChannelId == ChannelIds.Voice;
        string link;
        try
        {
            link = await handoffs.BeginAsync(
                ((PersonResolution.Found)resolution).Person, context, uri.ToString(), inChatClient: voice, delegation, ct);
        }
        catch (PersonStateUnreadableException ex)
        {
            return LoginLinks.Unreadable(ex);
        }

        return voice
            ? new JsonObject
            {
                ["status"] = "handoff_started",
                ["deliveredTo"] = "webchat",
                ["site"] = uri.Host,
                ["hint"] = "The link is in a new WebChat conversation for them. Tell them to open WebChat to log "
                           + "in, and stop; you are woken here, on this satellite, when they press Done."
            }
            : new JsonObject
            {
                ["status"] = "handoff_started",
                ["link"] = link,
                ["site"] = uri.Host,
                ["expiresInMinutes"] = handoffs.LinkMinutes,
                ["hint"] = "Give the person this link and stop. It works once; they log in on it and press "
                           + "Done, and you are woken in this conversation to carry on as them."
            };
    }

    // A turn nobody sent — a schedule, a watch, the login handoff's own turns — has nobody to open
    // a link, and a link minted there would sit in a report nobody asked for. Refused before
    // anything is opened.
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
        MessageOriginKind.Login => "the login handoff",
        _ => "something other than the person"
    };
}

// The page where a person sees and revokes their logins. The model can hand them its link; it
// cannot read the list or forget a login itself, so a page cannot talk it into revoking a session
// or covering its tracks.
public class WebLoginsTool(ILoginsPages pages, ILoginHandoffs handoffs)
{
    public const string Name = "web_logins";

    protected const string Description =
        "Answers a one-time link to a page where the person sees the sites the agent is logged into as them, and forgets any of them.";

    protected async Task<JsonObject> RunAsync(ConversationContext? context, CancellationToken ct)
    {
        var resolution = LoginLinks.Resolve(context, handoffs.IsConfigured);
        if (resolution is PersonResolution.Refused refused)
        {
            return refused.Error;
        }

        if (context!.Origin.ChannelId == ChannelIds.Voice)
        {
            return ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "The logins page is a link, and a voice reply cannot show one.",
                $"Tell them to ask for it {PersonResolution.WhereToAsk}.");
        }

        return new JsonObject
        {
            ["status"] = "link_ready",
            ["link"] = await pages.MintAsync(((PersonResolution.Found)resolution).Person, ct),
            ["expiresInMinutes"] = handoffs.LinkMinutes,
            ["hint"] = "Give the person this link. It opens once; only they can see or forget their logins there."
        };
    }
}

// What both link tools refuse alike, in the order a caller reads it: no turn behind the call, a
// turn nobody sent, nobody to give the link to, nowhere to point it. Otherwise the person found.
internal static class LoginLinks
{
    public static PersonResolution Resolve(ConversationContext? context, bool configured)
    {
        if (context is null)
        {
            return new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot tell who is logging in."));
        }

        if (WebLoginTool.RefuseFire(context) is { } fire)
        {
            return new PersonResolution.Refused(fire);
        }

        var resolution = PersonResolution.Of(context);
        return resolution is PersonResolution.Refused || configured
            ? resolution
            : new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "Logins are not set up on this deployment: it has no public address for the link.",
                "Tell the person their accounts cannot be reached until the operator sets the handoff address."));
    }

    public static bool IsWebUrl(string url, out Uri uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https";

    public static JsonObject Unreadable(PersonStateUnreadableException ex) => ToolError.Create(
        ToolError.Codes.UnsupportedOperation, ex.Message,
        "Tell the person their saved logins cannot be opened on this server until the operator fixes the key.");
}