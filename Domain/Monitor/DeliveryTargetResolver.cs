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
        if (message.ReplyTo is { Count: > 0 } replyTo)
        {
            return await FanOutAsync(message, replyTo, ct);
        }

        // A review copy is the same fan-out with the sending channel as its first target: the
        // conversation it already has anchors, and the review host attaches to that id on every
        // turn — showing it as a topic the first time, announcing the turn on it after. So the
        // channel's own conversation is the one the history, the approvals and the context name,
        // and nothing the channel waits on depends on the host being there (ADR 0046).
        var host = message.ReviewCopy is null
            ? null
            : channels.FirstOrDefault(c => c.HostsReviewCopies && c.ChannelId != message.ChannelId);
        if (host is null)
        {
            return [new DeliveryTarget(originChannel, message.ConversationId)];
        }

        var targets = await FanOutAsync(
            message,
            [new ReplyTarget(message.ChannelId, message.ConversationId), new ReplyTarget(host.ChannelId, null)],
            ct);
        if (message.Attachments is { Count: > 0 } attachments
            && targets.FirstOrDefault(t => t.Channel == host) is { Channel: not null } review)
        {
            // Started, not awaited: the channel's answer must never wait on the review host. A clip's
            // stills stay behind: the review shows what a video model was sent, which is the clip.
            _ = CopyAttachmentsAsync(originChannel, review, ClipStills.Without(attachments), ct);
        }

        return targets;
    }

    // The history the channel and the host share names each file by the channel's reference, and
    // the bytes rest where only the channel and the agent can reach them. So the host is handed a
    // copy under that same reference — the agent being the one party connected to both — and the
    // message and the history still carry references alone (ADR 0020, ADR 0046). One at a time, in
    // the order sent, so at most one file is held here at once. Never faults: a copy that fails
    // costs the review a picture and nothing else.
    private async Task CopyAttachmentsAsync(
        IChannelConnection origin, DeliveryTarget review, IReadOnlyList<AttachmentReference> attachments,
        CancellationToken ct)
    {
        try
        {
            foreach (var attachment in attachments)
            {
                var bytes = await origin.FetchAttachmentAsync(attachment.Id, ct);
                var kept = bytes is not null
                           && await review.Channel.StoreAttachmentAsync(review.ConversationId, attachment, bytes, ct);
                if (!kept)
                {
                    logger.LogWarning(
                        "Attachment {AttachmentId} was not copied to {ChannelId}; its review shows a placeholder",
                        attachment.Id, review.Channel.ChannelId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Copying attachments to {ChannelId} for review failed", review.Channel.ChannelId);
        }
    }

    private async Task<IReadOnlyList<DeliveryTarget>> FanOutAsync(
        ChannelMessage message, IReadOnlyList<ReplyTarget> replyTargets, CancellationToken ct)
    {
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
        var replyTo = replyTargets
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
    // name on the origin; a schedule never had one and keeps the label it always had. A review copy
    // is named by the channel that asked for it.
    private static string TopicName(ChannelMessage message) =>
        message.ReviewCopy?.Title is { Length: > 0 } review ? review
        : message.Origin?.Title is { Length: > 0 } title ? title
        : "Scheduled task";

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
                    // Empty, and it has to be: an existing id WITH a name is a later fan-out target,
                    // which SignalR shows by minting a topic over it when none holds it (ADR 0046).
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
        // An unprompted fire runs on behalf of the person its author named — a schedule's or a
        // watch's `userId` — not as the sender label its server stamps on it ("scheduler",
        // "watch"); a message a person sent themselves names nobody, and the sender is that person.
        return new ConversationContext(
            message.AgentId ?? "default",
            conversationId,
            message.UserId ?? message.Sender,
            new ReplyTarget(channelId, conversationId, address),
            message.ConfigPatch?.Model,
            message.Origin?.Kind);
    }
}