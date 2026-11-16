using Domain.DTOs.Channel;
using Domain.Security;

namespace Domain.Tools.Web;

// A login handoff as a delegation describes it: the turn that asked — its agent, its conversation and
// where it answers — and the site it asked to log in to, read on both ends, the agent's off the
// web_login call it sends and the browse server's off the handoff it keeps. Named by the conversation
// it wakes, the one id both ends know before the server has opened anything.
//
// The wake's words are the handoff's own, spelled here once for both ends, so the agent can tell a
// wake that says them for the delegated site from one a browse server wrote: Done's carry on as the
// person; the link's put the one-time link in front of them, which rides beside the words as the
// fire's report (it is minted by the browse server and no delegation can name it in advance).
public static class LoginDelegation
{
    // Null for a url no handoff opens — the tool refuses it before anything is delegated.
    public static DelegatedObject? Of(ConversationContext asking, string url) =>
        SiteOf(url) is { } site
            ? DelegatedObject.Of(DelegatedObject.Login, asking.ConversationId,
                new DelegatedLogin(asking.AgentId, asking.ConversationId, asking.Origin, site))
            : null;

    public static string? SiteOf(string url) => LoginLinks.IsWebUrl(url, out var uri) ? uri.Host : null;

    public static string DoneText(string site) =>
        $"[Login handoff finished] The person has logged in to {site} in their own browser and pressed Done. "
        + "Carry on with what they asked, browsing with asMe; do not ask them to confirm.";

    public static string LinkText(string site) =>
        $"[Login link for {site}] The person asked on a voice satellite for something that needs them logged in "
        + $"to {site}. Give them the link this turn's report carries, and nothing else — once they log in there "
        + "and press Done, the answer comes back on the satellite.";

    public static string LinkReport(string link, int minutes) =>
        $"The login link, which opens once and for {minutes} minutes: {link}";
}