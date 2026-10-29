using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerSugary.Settings;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Channel;

// The app's side of a turn: send the person's words to the agent as a channel message, and wait for
// the whole reply to the turn it opened. Which agent answers is left to the deployment's routing
// (agentDefaults), so the message names none; a patch may still move that one turn to another of the
// agent's models, as a label's does.
//
// A turn acts for the person signed in on the phone that asked: its principal is their account, the
// account's id standing as the channel's own name for them, since the proxy's headers say nothing
// more; and it goes by that id too, the only name the server has for them.
public sealed class SugaryChannel(
    ChannelNotificationEmitter emitter, AgentTurns turns, TimeProvider clock, SugarySettings settings,
    ILogger<SugaryChannel> logger)
{
    public const string ChannelId = "sugary";

    // Who a turn from the app is from where no person is behind it: the eval's and the benchmark's
    // turns, which stand in for the app.
    public const string Sender = "sugary-app";

    public async Task<TurnReply> AskAsync(
        Account person, string conversationId, string content, ResponseFormat format,
        IReadOnlyList<AttachmentReference> attachments, AgentConfigPatch? patch, ReviewCopy? review,
        CancellationToken ct)
    {
        using var turn = turns.Open();
        var delivered = await emitter.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = person.UserId,
            Principal = new Principal(ChannelId, person.UserId, person),
            Content = content,
            ResponseFormat = format,
            ConfigPatch = patch,
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
            var reply = await turn.Reply.WaitAsync(TimeSpan.FromSeconds(settings.TurnTimeoutSeconds), clock, ct);
            // The agent's reason is the exception it caught, often with the provider's own body, and
            // in English: kept here for whoever looks into it, never sent on to her screen.
            if (reply is TurnReply.Failed failed)
            {
                logger.LogWarning("The agent failed turn {TurnKey} of {ConversationId}: {Reason}",
                    turn.TurnKey, conversationId, failed.Reason);
            }

            return reply;
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