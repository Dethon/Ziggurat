using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;

namespace Domain.Tools.Channels;

// The transport half of channel_receive, shared by every channel server: each one exposes it
// through a thin Mcp* wrapper in its own McpTools folder (the tool's name lives in
// ChannelProtocol.ReceiveTool next to the rest of the wire contract), so channel servers that
// need nothing else from Infrastructure can reference Domain alone.
public class ChannelReceiveTool(ChannelInbox inbox)
{
    protected const string Description = "Internal channel transport. Long-polls for inbound channel items.";
    protected const string SubscriberIdDescription = "Stable subscriber id, e.g. channel-signalr";
    protected const string MaxWaitMsDescription = "How long to hold the request open, in milliseconds";
    protected const string PollerDescription =
        "Optional. An id the poller minted for itself, the same on every poll it makes; with it, a batch whose answer never arrived is sent again";
    protected const string ReceivedDescription =
        "Optional, with poller. The number of the last batch this poller received (0 before its first)";

    // Clamped, not just conventionally short today: ChannelInbox.LiveSubscriberFreshness
    // gives a subscriber headroom for one fully held poll plus one retry backoff on the
    // assumption that no single poll holds the request open longer than DefaultReceiveWaitMs
    // (a subscriber is stamped when its poll *starts* — see ChannelInbox.Subscriber's own doc
    // comment). An unclamped maxWaitMs from a future/misbehaving caller could park a genuinely
    // live subscriber past the freshness window, making HasLiveSubscriber read it as dead —
    // worse than the bug the freshness check exists to fix.
    protected Task<string> Run(string subscriberId, int maxWaitMs, CancellationToken cancellationToken) =>
        Run(subscriberId, maxWaitMs, null, null, cancellationToken);

    // A poller that names itself is answered with the batch's number, which it names as received on its next
    // poll (ChannelInbox's PollerCursor); one that names nothing is answered as before, every batch committed
    // as it leaves.
    protected Task<string> Run(
        string subscriberId, int maxWaitMs, string? poller, long? received, CancellationToken cancellationToken)
    {
        var clampedWaitMs = Math.Clamp(maxWaitMs, 0, ChannelProtocol.DefaultReceiveWaitMs);
        // The response body is written inside the poll's turn at the queue, not after it: the batch
        // is never out of the inbox's hands between leaving the queue and becoming this string, so a
        // response that turns out to be dead puts its items back before any other poll can drain.
        return inbox.ReceiveAsync(
            subscriberId, TimeSpan.FromMilliseconds(clampedWaitMs), Serialize, cancellationToken,
            string.IsNullOrEmpty(poller) ? null : new PollerCursor(poller, received ?? 0));
    }

    private static string Serialize(IReadOnlyList<ChannelInboxItem> items, long? batch) =>
        JsonSerializer.Serialize(
            new ChannelReceiveResult { Items = items, Batch = batch }, ChannelProtocol.SerializerOptions);
}