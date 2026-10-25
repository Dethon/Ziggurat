using Domain.DTOs.Channel;

namespace McpServerWebSearch.Handoff;

// The turn Done raises: the conversation, agent and person that asked for the login, answering to
// the reply target that turn had — a satellite's address included — so it joins the live
// conversation (queued behind a running turn) or, when none is live, goes where that turn would
// have gone. Its origin is Login, so it cannot start another handoff itself.
public static class LoginWake
{
    public const string Sender = "login";

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