using System.Text.Json;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.WebChat;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

// Internal because the turn is: this is the one place a reply's wire record is built, and it is
// built out of the turn that produced the update.
// readImages: where the pictures a tool showed the model rest, copied from there to a channel that draws
// tool calls; none on a host keeping no read images, whose calls then name no pictures.
internal class ReplyDispatcher(ILogger logger, IReadImageStore? readImages = null)
{
    // What delivering one update came to: whether any of its content reached a target, the turn keys a
    // channel answered were handed back by a stop (ReplyReceipt), which the conversation must not run, and
    // the steers it drew together at a turn's end, which the conversation runs as one turn.
    public sealed record Delivery(
        bool DeliveredContent, IReadOnlyList<string> ReturnedTurnKeys, IReadOnlyList<string> SteeredTogether);

    public async Task<Delivery> DeliverUpdateAsync(
        AgentResponseUpdate update, Turn turn, CancellationToken ct)
    {
        var deliveredContent = false;
        List<string> returned = [];
        IReadOnlyList<string> together = [];
        foreach (var mapped in MapResponseUpdate(update))
        {
            var receipts = await Task.WhenAll(turn.Targets
                .Where(target => Shows(target.Channel, mapped.ContentType))
                .Select(target => DeliverToTargetAsync(target, mapped, update.MessageId, turn, ct)));

            // A tool call is no reply yet: the first reply is still the first thing said.
            deliveredContent |= mapped.ContentType is not (ReplyContentType.StreamComplete
                                    or ReplyContentType.ToolCall or ReplyContentType.ToolResult)
                                && receipts.Any(r => r is not null);
            returned.AddRange(receipts.SelectMany(r => r?.ReturnedTurnKeys ?? []));
            together = receipts.Select(r => r?.SteeredTogether).FirstOrDefault(keys => keys is { Count: > 0 }) ?? together;
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

        return new Delivery(deliveredContent, returned, together);
    }

    private static bool Shows(IChannelConnection channel, ReplyContentType kind) => kind switch
    {
        ReplyContentType.Reasoning => channel.ShowsReasoning,
        ReplyContentType.ToolCall or ReplyContentType.ToolResult => channel.ShowsToolCalls,
        _ => true
    };

    // The one place a reply's wire record is built. Everything a chunk says is decided above; the
    // conversation is the one part that belongs to the target rather than to the chunk, and the
    // turn key is what lets the far end tell this turn's answer from a previous one's.
    // Null when the target could not be delivered to.
    private async Task<ReplyReceipt?> DeliverToTargetAsync(
        DeliveryTarget target, MappedChunk mapped, string? messageId, Turn turn, CancellationToken ct)
    {
        try
        {
            var content = mapped.Answer is { } answer ? await ToolResultForAsync(answer, target, turn, ct) : mapped.Content;
            return await target.Channel.SendReplyAsync(
                new SendReplyParams
                {
                    ConversationId = target.ConversationId,
                    Content = content,
                    ContentType = mapped.ContentType,
                    IsComplete = mapped.IsComplete,
                    MessageId = messageId,
                    TurnKey = turn.TurnKey,
                    AgentInitiated = turn.AgentInitiated
                },
                ct);

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
            return null;
        }
    }

    private static string Wire(ToolCallStep step) => JsonSerializer.Serialize(step, ChannelProtocol.SerializerOptions);

    // A call's answer is written per target: the pictures it showed the model are copied to that target
    // first, under the references its own conversation names them by (ToolImages), and the step names them.
    // A picture whose bytes are gone, or that the channel would not keep, is left out rather than failing
    // the answer.
    private async Task<string> ToolResultForAsync(FunctionResultContent answer, DeliveryTarget target, Turn turn, CancellationToken ct)
    {
        var step = ToolSteps.Result(answer);
        if (readImages is null || turn.Targets.Count == 0)
        {
            return Wire(step);
        }

        // The model's pictures are kept under the conversation the turn's context names, its first target's.
        var keyedUnder = turn.Targets[0].ConversationId;

        List<AttachmentReference> kept = [];
        foreach (var image in ToolImages.Of(answer, target.ConversationId))
        {
            try
            {
                if (await readImages.GetAsync(keyedUnder, image.StoreKey, ct) is { } read)
                {
                    var reference = image.Reference with { SizeBytes = read.Bytes.LongLength };
                    await target.Channel.StoreAttachmentAsync(target.ConversationId, reference, read.Bytes, ct);
                    kept.Add(image.Reference);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not copy the picture {Key} to {ChannelId}", image.StoreKey, target.Channel.ChannelId);
            }
        }

        return Wire(kept.Count > 0 ? step with { Images = kept } : step);
    }

    // Answer: a call's answer, whose wire record is written per target (ToolResultForAsync).
    private sealed record MappedChunk(
        string Content, ReplyContentType ContentType, bool IsComplete, FunctionResultContent? Answer = null);

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
                // A call as the model wrote it, and its answer once it ran, shaped as a page reads them.
                FunctionCallContent call
                    => new MappedChunk(Wire(ToolSteps.Call(call)), ReplyContentType.ToolCall, false),
                FunctionResultContent result
                    => new MappedChunk("", ReplyContentType.ToolResult, false, result),
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