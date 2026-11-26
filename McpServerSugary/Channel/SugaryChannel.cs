using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerSugary.Settings;

namespace McpServerSugary.Channel;

// The app's side of a turn: send the person's words to the agent as a channel message, and wait for
// the whole reply to the turn it opened. Which agent answers is left to the deployment's routing
// (agentDefaults), so the message names none; a patch may still move that one turn to another of the
// agent's models, as a label's does.
//
// A turn acts for the person signed in on the phone that asked: its principal is their account, the
// account's id standing as the channel's own name for them, since the proxy's headers say nothing
// more; and it goes by that id too, the only name the server has for them.
public sealed class SugaryChannel(AgentTurns turns, TimeProvider clock, SugarySettings settings)
{
    public const string ChannelId = ChannelIds.Sugary;

    // Who a turn from the app is from where no person is behind it: the eval's and the benchmark's
    // turns, which stand in for the app.
    public const string Sender = ServiceSenders.SugaryApp;

    public Task<TurnReply> AskAsync(
        Account person, string assertion, string conversationId, string content, ResponseFormat format,
        IReadOnlyList<AttachmentReference> attachments, AgentConfigPatch? patch, ReviewCopy? review,
        CancellationToken ct) =>
        turns.SendAsync(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = person.UserId,
            Principal = new Principal(ChannelId, person.UserId, person),
            // The proxy's assertion of the person, from the request that asked: what the agent believes.
            Assertion = assertion,
            Content = content,
            ResponseFormat = format,
            ConfigPatch = patch,
            // Shown in WebChat as well, so what was sent and what came back can be read afterwards.
            ReviewCopy = review,
            Attachments = attachments.Count > 0 ? attachments : null,
            Timestamp = clock.GetUtcNow()
        }, TimeSpan.FromSeconds(settings.TurnTimeoutSeconds), clock, ct);
}