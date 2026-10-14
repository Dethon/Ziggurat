using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerSugary.Settings;

namespace McpServerSugary.Channel;

// The app's side of a turn: send her words to the agent as a channel message, and wait for the
// whole reply to the turn it opened. Which agent answers is left to the deployment's routing
// (agentDefaults), so the message names none.
public sealed class SugaryChannel(
    ChannelNotificationEmitter emitter, AgentTurns turns, TimeProvider clock, SugarySettings settings)
{
    // Who a turn from the app is from, on the dashboard's token totals and in the conversation.
    public const string Sender = "sugary-app";

    public async Task<TurnReply> AskAsync(
        string conversationId, string content, ResponseFormat format,
        IReadOnlyList<AttachmentReference> attachments, ReviewCopy? review, CancellationToken ct)
    {
        using var turn = turns.Open();
        var delivered = await emitter.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = Sender,
            Content = content,
            ResponseFormat = format,
            // Shown in WebChat as well, so what was sent and what came back can be read afterwards.
            ReviewCopy = review,
            Attachments = attachments.Count > 0 ? attachments : null,
            TurnKey = turn.TurnKey,
            Timestamp = clock.GetUtcNow()
        }, ct);
        if (!delivered)
        {
            return new TurnReply.NotListening();
        }

        try
        {
            return await turn.Reply.WaitAsync(TimeSpan.FromSeconds(settings.TurnTimeoutSeconds), clock, ct);
        }
        catch (TimeoutException)
        {
            await CancelAsync(conversationId);
            return new TurnReply.TimedOut();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The phone gave up; an answer nobody will read is not worth the rest of the turn.
            await CancelAsync(conversationId);
            throw;
        }
    }

    private Task<bool> CancelAsync(string conversationId) =>
        emitter.EmitCancelAsync(new ChannelCancelNotification
        {
            ConversationId = conversationId,
            Timestamp = clock.GetUtcNow()
        });
}