using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class DeliveryTargetResolver(IReadOnlyList<IChannelConnection> channels, ILogger logger)
{
    public async Task<IReadOnlyList<DeliveryTarget>> ResolveAsync(
        ChannelMessage message,
        IChannelConnection originChannel,
        CancellationToken ct)
    {
        if (message.ReplyTo is not { Count: > 0 })
        {
            return [new DeliveryTarget(originChannel, message.ConversationId)];
        }

        var targets = new List<DeliveryTarget>();
        // The first resolved conversation anchors a shared id for the whole fan-out. A
        // schedule delivering to both a WebChat channel and voice should surface as ONE
        // conversation — displayed by WebChat and spoken by voice — not a populated thread
        // plus an empty duplicate. Later targets that need minting attach to this id (the
        // voice channel binds its satellite to it instead of persisting its own topic).
        //
        // Attach-only channels (a config-declared capability, e.g. voice) return the id they
        // are handed instead of persisting a topic, so a topic-owning channel must anchor:
        // order attach-only targets last regardless of how the schedule listed them. This
        // also makes targets[0] — the chat-history persistence + approval-routing anchor — a
        // channel that actually displays the conversation. OrderBy is stable, so the
        // author's ordering is otherwise preserved.
        var replyTo = message.ReplyTo
            .OrderBy(t => channels.FirstOrDefault(c => c.ChannelId == t.ChannelId)?.AttachOnly == true ? 1 : 0)
            .ToList();
        string? shared = null;
        foreach (var target in replyTo)
        {
            var channel = channels.FirstOrDefault(c => c.ChannelId == target.ChannelId);
            if (channel is null)
            {
                continue;
            }

            var conversationId = target.ConversationId;
            var wasMinted = false;
            if (conversationId is null)
            {
                try
                {
                    conversationId = await channel.CreateConversationAsync(
                        message.AgentId ?? "default", TopicName(message), message.Sender, message.Content, target.Address, shared, ct);
                    wasMinted = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A target whose conversation can't be minted is skipped rather than
                    // aborting the whole fan-out (and the agent run that depends on it).
                    logger.LogWarning(ex, "Failed to mint conversation on {ChannelId}; skipping target", target.ChannelId);
                    continue;
                }
            }

            if (conversationId is not null)
            {
                shared ??= conversationId;
                targets.Add(new DeliveryTarget(channel, conversationId, Minted: wasMinted, Address: target.Address));
            }
        }

        return targets;
    }

    // The sidebar label of a conversation minted for an unprompted fire. A watch carries its human
    // name on the origin; a schedule never had one and keeps the label it always had.
    private static string TopicName(ChannelMessage message) =>
        message.Origin?.Title is { Length: > 0 } title ? title : "Scheduled task";

    public async Task AnnounceTurnStartAsync(
        IReadOnlyList<DeliveryTarget> targets,
        ChannelMessage message,
        CancellationToken ct)
    {
        // The announce is channel-agnostic: every target gets the same create_conversation
        // call and applies its own turn-start semantics (SignalR sets up a live stream,
        // voice binds an announcement unless the satellite session is live). Channels
        // without a create_conversation tool no-op inside CreateConversationAsync.
        //
        // A target minted while resolving THIS turn was already announced by its own
        // create_conversation, so announcing it again would set the same stream up twice.
        var announceable = targets.Where(t => !t.Minted);
        foreach (var target in announceable)
        {
            try
            {
                await target.Channel.CreateConversationAsync(
                    message.AgentId ?? "default",
                    topicName: string.Empty,
                    message.Sender,
                    initialPrompt: message.Content,
                    address: target.Address,
                    existingConversationId: target.ConversationId,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reply itself is persisted regardless; a failed announce only costs
                // live streaming, so it must never abort the turn.
                logger.LogWarning(ex, "Turn-start announce to {ChannelId} failed; reply will not stream live",
                    target.Channel.ChannelId);
            }
        }
    }

    public static ConversationContext BuildConversationContext(
        ChannelMessage message, IReadOnlyList<DeliveryTarget> targets)
    {
        var (channelId, conversationId) = targets.Count > 0
            ? (targets[0].Channel.ChannelId, targets[0].ConversationId)
            : (message.ChannelId, message.ConversationId);
        var address = channelId == message.ChannelId ? message.SatelliteId : null;
        return new ConversationContext(
            message.AgentId ?? "default",
            conversationId,
            message.Sender,
            new ReplyTarget(channelId, conversationId, address));
    }
}