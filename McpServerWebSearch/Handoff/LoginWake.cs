using Domain.DTOs.Channel;
using Domain.Tools.Web;

namespace McpServerWebSearch.Handoff;

// The turn Done raises: the conversation, agent and person that asked for the login, answering to
// the reply target that turn had — a satellite's address included — so it joins the live
// conversation (queued behind a running turn) or, when none is live, goes where that turn would
// have gone. Its origin is Login, so it cannot start another handoff itself.
public static class LoginWake
{
    public const string Sender = "login";

    // A voice turn's link, put where the person can tap it: a new conversation of their chat
    // client, whose end-of-stream Web Push pings them. It acts for the account the voice turn did —
    // the principal this server kept, as the wake's — which is whose conversation it is; the person
    // goes by their account's id here because this server learns no display name. The turn presents
    // the link and nothing else; the answer to what they asked comes back on the satellite once they
    // press Done.
    public static ChannelMessageNotification LinkInChatClient(
        LoginHandoffs.LiveHandoff handoff, string link, int minutes, DateTimeOffset at)
    {
        var site = new Uri(handoff.Url).Host;
        return new ChannelMessageNotification
        {
            ConversationId = $"login-{handoff.Id}-{at.ToUnixTimeMilliseconds()}",
            Sender = handoff.Person,
            Content = $"[Login link for {site}] The person asked on a voice satellite for something that needs "
                      + $"them logged in to {site}. Give them this link, which opens once and for {minutes} "
                      + $"minutes, and nothing else: {link} — once they log in there and press Done, the answer "
                      + "comes back on the satellite.",
            AgentId = handoff.Context.AgentId,
            UserId = handoff.Person,
            Principal = Principal.Of(handoff.Context),
            Delegation = DelegationOf(handoff),
            ReplyTo = [new ReplyTarget(ChannelIds.SignalR, null)],
            Origin = new MessageOrigin(MessageOriginKind.Login, null, Title: $"Log in to {site}"),
            Timestamp = at
        };
    }

    private static FireDelegation? DelegationOf(LoginHandoffs.LiveHandoff handoff) =>
        handoff.Delegation is { } token ? new FireDelegation(token, LoginDelegation.Of(handoff.Context).Description) : null;

    public static ChannelMessageNotification Compose(LoginHandoffs.LiveHandoff handoff, DateTimeOffset at)
    {
        var site = new Uri(handoff.Url).Host;
        return new ChannelMessageNotification
        {
            ConversationId = handoff.Context.ConversationId,
            Sender = Sender,
            Content = $"[Login handoff finished] The person has logged in to {site} in their own browser and "
                      + "pressed Done. Carry on with what they asked, browsing with asMe; do not ask them "
                      + "to confirm.",
            AgentId = handoff.Context.AgentId,
            UserId = handoff.Context.UserId,
            // The wake is a turn nobody sent: it acts for the person only through the delegation the
            // agent signed as the asking call left, which this server kept beside the handoff. The
            // principal is the asking turn's, a label the agent does not believe.
            Principal = Principal.Of(handoff.Context),
            Delegation = DelegationOf(handoff),
            ReplyTo = [handoff.Context.Origin],
            Origin = new MessageOrigin(MessageOriginKind.Login, null, Title: $"Login to {site}"),
            Timestamp = at
        };
    }
}