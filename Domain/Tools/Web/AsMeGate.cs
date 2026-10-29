using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Domain.Tools.Web;

// Who an asMe browse runs as, and whether it may: the turn resolved through the identity map, then
// the site checked against the person's logins. asMe is a flag the model sets, and a page's text can
// talk it into setting one, so the person's session reaches only the sites they handed off; the
// agent's approval gate then asks them about each such site once per conversation.
public class AsMeGate(IdentityMap identities, ILoginSites logins)
{
    public async Task<PersonResolution> ResolveAsync(ConversationContext? context, string url, CancellationToken ct)
    {
        var resolution = identities.Resolve(context);
        if (resolution is not PersonResolution.Found found)
        {
            return resolution;
        }

        bool loggedIn;
        try
        {
            loggedIn = await logins.HasLoginAsync(found.Person, url, ct);
        }
        catch (PersonStateUnreadableException ex)
        {
            return new PersonResolution.Refused(LoginLinks.Unreadable(ex));
        }

        return loggedIn ? found : new PersonResolution.Refused(NotLoggedIn(found.Person, url, context!));
    }

    // On the person's own turn the way in is a login they do themselves; on a turn nobody sent there
    // is nobody to hand one to, so the turn reports it.
    private static JsonObject NotLoggedIn(string person, string url, ConversationContext context)
    {
        var site = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        return ToolError.Create(
            ToolError.Codes.PermissionDenied,
            $"'{person}' has no login on {site} here, so it cannot be browsed as them.",
            context.OriginKind is null
                ? "Call web_login with this url so they log in themselves, give them its link, and stop."
                : $"Say in your report that they need to log in to {site}, which starts when they ask you directly.");
    }
}