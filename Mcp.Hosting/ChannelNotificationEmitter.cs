using Domain.Channels;
using Domain.DTOs.Channel;

namespace Mcp.Hosting;

// The one way a channel server puts an item on the wire. Emitting answers "was anyone listening?"
// as its return value, with the freshness check inside the operation, so a caller can neither skip
// the question nor ask it a different way — which is how the same stale-subscriber defect landed
// independently in six hand-copied emitters.
//
// Sealed on purpose: substituting it in a test replaces the delivery path with an override of it.
// Tests construct a real ChannelInbox and drain it instead.
public sealed class ChannelNotificationEmitter
{
    private readonly ChannelInbox _inbox;
    private readonly DeliveryPolicy _policy;
    private readonly string? _subscriberId;

    public ChannelNotificationEmitter(ChannelInbox inbox, DeliveryPolicy policy, string? subscriberId = null)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        DeliveryPolicyRules.ValidateSubscriberId(policy, subscriberId);
        _inbox = inbox;
        _policy = policy;
        _subscriberId = subscriberId;
    }

    public DeliveryPolicy Policy => _policy;

    // The token is honoured, not decorative: the callers that pass a real one settle a durable
    // record on the answer — a schedule is deleted or stamped, a routing entry removed, a broker
    // message completed. Delivering on the way down and reporting live would settle those records
    // for an item that reached nobody, so a cancelled emit delivers nothing and throws.
    public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Deliver(ChannelInboxItem.ForMessage(payload)));
    }

    // The broadcast emit with both of its answers, for a caller answering an outside party — the
    // watch callback telling Home Assistant whether a fire was taken. Accepted means a registered
    // subscriber holds a copy, even a quiet one mid-reconnect; only "nobody registered at all" is a
    // loss. Defined for Broadcast alone: the other two policies never buffer for a quiet subscriber,
    // so their accepted and live answers are one answer.
    public Task<EnqueueReceipt> EmitWithReceiptAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_policy != DeliveryPolicy.Broadcast)
        {
            throw new InvalidOperationException(
                "EmitWithReceiptAsync is defined for the Broadcast policy: the other policies buffer nothing a live answer would not say.");
        }

        return Task.FromResult(_inbox.EnqueueWithReceipt(ChannelInboxItem.ForMessage(payload)));
    }

    public Task<bool> EmitCancelAsync(ChannelCancelNotification payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Deliver(ChannelInboxItem.ForCancel(payload)));
    }

    // One call per policy, and each of them answers liveness about the item it just took: a check
    // this method made first would be about a moment that has passed by the time the item lands, and
    // the callers that settle a durable record on the answer would settle it for an item that
    // reached nobody. Which subscribers an item goes to is the policy's difference; who was
    // listening is never a second question.
    private bool Deliver(ChannelInboxItem item) => _policy switch
    {
        DeliveryPolicy.BufferAlways => _inbox.EnqueueFor(_subscriberId!, item),
        DeliveryPolicy.GateOnLive => _inbox.EnqueueIfLive(item),
        _ => _inbox.Enqueue(item)
    };
}