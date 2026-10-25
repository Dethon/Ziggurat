using Domain.DTOs.Channel;

namespace McpServerWebSearch.Handoff;

// The turn Done raises: the conversation, agent and person that asked for the login, answering to
// the reply target that turn had — a satellite's address included — so it joins the live
// conversation (queued behind a running turn) or, when none is live, goes where that turn would
// have gone. Its origin is Login, so it cannot start another handoff itself.
public static class LoginWake
{
    public const string Sender = "login";

    // A voice turn's link, put where the person can tap it: a new WebChat conversation of theirs,
    // whose end-of-stream Web Push pings them. The turn presents the link and nothing else; the
    // answer to what they asked comes back on the satellite once they press Done.
    public static ChannelMessageNotification LinkInWebChat(
        LoginHandoffs.LiveHandoff handoff, string link, string webChatName, int minutes, DateTimeOffset at)
    {
        var site = new Uri(handoff.Url).Host;
        return new ChannelMessageNotification
        {
            ConversationId = $"login-{handoff.Id}-{at.ToUnixTimeMilliseconds()}",
            Sender = webChatName,
            Content = $"[Login link for {site}] The person asked on a voice satellite for something that needs "
                      + $"them logged in to {site}. Give them this link, which opens once and for {minutes} "
                      + $"minutes, and nothing else: {link} — once they log in there and press Done, the answer "
                      + "comes back on the satellite.",
            AgentId = handoff.Context.AgentId,
            UserId = webChatName,
            ReplyTo = [new ReplyTarget(Domain.Tools.Web.WebLoginTool.WebChatChannelId, null)],
            Origin = new MessageOrigin(MessageOriginKind.Login, null, Title: $"Log in to {site}"),
            Timestamp = at
        };
    }

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
            ReplyTo = [handoff.Context.Origin],
            Origin = new MessageOrigin(MessageOriginKind.Login, null, Title: $"Login to {site}"),
            Timestamp = at
        };
    }
}