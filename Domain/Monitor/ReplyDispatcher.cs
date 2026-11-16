using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

// Internal because the turn is: this is the one place a reply's wire record is built, and it is
// built out of the turn that produced the update.
internal class ReplyDispatcher(ILogger logger)
{
    public async Task<bool> DeliverUpdateAsync(
        AgentResponseUpdate update, Turn turn, CancellationToken ct)
    {
        var deliveredContent = false;
        foreach (var mapped in MapResponseUpdate(update))
        {
            var results = await Task.WhenAll(turn.Targets.Select(target =>
                DeliverToTargetAsync(target, mapped, update.MessageId, turn, ct)));
            deliveredContent |= mapped.ContentType != ReplyContentType.StreamComplete && results.Any(r => r);
        }

        foreach (var error in update.Contents.OfType<ErrorContent>())
        {
            turn.Metrics.Publish(new ErrorEvent
            {
                Service = "agent",
                ErrorType = error.ErrorCode ?? "Unknown",
                Message = error.Message
            });
        }

        return deliveredContent;
    }

    // The one place a reply's wire record is built. Everything a chunk says is decided above; the
    // conversation is the one part that belongs to the target rather than to the chunk, and the
    // turn key is what lets the far end tell this turn's answer from a previous one's.
    private async Task<bool> DeliverToTargetAsync(
        DeliveryTarget target, MappedChunk mapped, string? messageId, Turn turn, CancellationToken ct)
    {
        try
        {
            await target.Channel.SendReplyAsync(
                new SendReplyParams
                {
                    ConversationId = target.ConversationId,
                    Content = mapped.Content,
                    ContentType = mapped.ContentType,
                    IsComplete = mapped.IsComplete,
                    MessageId = messageId,
                    TurnKey = turn.TurnKey,
                    AgentInitiated = turn.AgentInitiated
                },
                ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Isolate per-target delivery failures: one channel being down must not
            // abort delivery to the other targets or tear down the agent run (which
            // would also suppress its schedule-execution metric).
            logger.LogWarning(ex, "Failed to deliver reply to {ChannelId}; skipping target",
                target.Channel.ChannelId);
            turn.Metrics.Publish(new ErrorEvent
            {
                Service = "agent",
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            });
            return false;
        }
    }

    private sealed record MappedChunk(string Content, ReplyContentType ContentType, bool IsComplete);

    private static IEnumerable<MappedChunk> MapResponseUpdate(AgentResponseUpdate update)
    {
        foreach (var aiContent in update.Contents)
        {
            MappedChunk? mapped = aiContent switch
            {
                TextContent text when !string.IsNullOrEmpty(text.Text)
                    => new MappedChunk(text.Text, ReplyContentType.Text, false),
                TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text)
                    => new MappedChunk(reasoning.Text, ReplyContentType.Reasoning, false),
                // FunctionCallContent is intentionally skipped — tool calls are displayed
                // by the approval flow (request_approval tool with mode=request or mode=notify)
                ErrorContent error
                    => new MappedChunk(error.Message, ReplyContentType.Error, false),
                StreamCompleteContent
                    => new MappedChunk(string.Empty, ReplyContentType.StreamComplete, true),
                _ => null
            };

            if (mapped is { } value)
            {
                yield return value;
            }
        }
    }
}