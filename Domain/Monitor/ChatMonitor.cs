using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IReadOnlyList<IChannelConnection> channels,
    IAgentFactory agentFactory,
    ChatThreadResolver threadResolver,
    IMetricsPublisher metricsPublisher,
    IMemoryRecallHook? memoryRecallHook,
    ILogger<ChatMonitor> logger,
    // Optional because a host may configure none — a test harness, or a deployment where every
    // channel names its agent. With none configured a message that names no agent is refused
    // when the agent is built, which is the point: nothing guesses.
    AgentDefaults? agentDefaults = null)
{
    private readonly DeliveryTargetResolver _targetResolver = new(channels, logger);
    private readonly ReplyDispatcher _replyDispatcher = new(metricsPublisher, logger);

    public async Task Monitor(CancellationToken cancellationToken)
    {
        try
        {
            var merged = channels
                .Select(ch => ch.Messages.Select(m => (Channel: ch, Message: RouteToAgent(m))))
                .Merge(cancellationToken);

            var groups = merged
                .GroupByStreaming(
                    (x, _) => ValueTask.FromResult(new AgentKey(x.Message.ConversationId, x.Message.AgentId)),
                    // A message can race into a group just as its teardown completes it; the
                    // write is refused, and the refusal must name the turn like any other drop.
                    x => ConversationGroup.LogDroppedTurn(logger, x.Message),
                    cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            await foreach (var _ in groups)
            { }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = "agent",
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            });
        }
    }

    // Resolved here, upstream of the grouping, so one id serves the whole turn: the group key, the
    // agent built for it, the conversation context stamped on the message and the memory written
    // from it. Resolving it later would let a message that named no agent group apart from one
    // that named the same agent explicitly, running two agents over one conversation.
    private ChannelMessage RouteToAgent(ChannelMessage message) =>
        string.IsNullOrEmpty(message.AgentId) && agentDefaults?.For(message.ChannelId) is { } fallback
            ? message with { AgentId = fallback }
            : message;

    private async IAsyncEnumerable<bool> ProcessChatThread(
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conversation = new ConversationGroup(
            agentKey, agentFactory, _targetResolver, threadResolver, metricsPublisher, memoryRecallHook, logger);

        await foreach (var turnUpdate in conversation.RunAsync(group, group.Complete, ct).WithCancellation(ct))
        {
            var deliveredContent = await _replyDispatcher.DeliverUpdateAsync(
                turnUpdate.Update, turnUpdate.Turn, ct);
            if (deliveredContent)
            {
                // Ends the span on the first delivered chunk; the scope publishes at most once, so
                // every later chunk of the same turn is a no-op. A turn that delivers nothing
                // never disposes it and records no first-reply latency.
                turnUpdate.Turn.FirstReply.Dispose();
            }

            yield return true;
        }
    }
}