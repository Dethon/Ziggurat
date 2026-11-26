using Domain.Channels;
using Domain.DTOs.Channel;
using Mcp.Hosting;

namespace Tests.Unit.Mcp.Hosting;

// A real ChannelInbox behind the real emitter, drained the way the agent's channel connection
// drains it. Every test that used to substitute an emitter uses this instead: overriding the
// emitter asserted against a stand-in for the delivery path rather than the path itself, and it
// kept a test seam alive in production code.
//
// Safe to emit into concurrently — the inbox is thread-safe and the accumulated snapshot is
// guarded — so a concurrency defect fails the assertion it belongs to instead of corrupting a list.
internal sealed class ChannelInboxProbe
{
    private readonly ChannelInbox _inbox = new();
    private readonly Lock _gate = new();
    private readonly List<ChannelMessageNotification> _received = [];
    private readonly List<ChannelCancelNotification> _cancels = [];
    private readonly string _subscriberId;

    // `live: false` leaves the subscriber unregistered, which is how production expresses "nobody
    // is listening" — the case the three delivery policies differ on.
    public ChannelInboxProbe(string channelId, DeliveryPolicy policy, bool live = true)
    {
        _subscriberId = ChannelProtocol.ChannelClientNamePrefix + channelId;

        // Registered before the emitter exists, because Broadcast only reaches subscribers that
        // are already there: a probe that registered lazily would miss the first message and read
        // as a delivery failure.
        if (live)
        {
            GoLive();
        }

        Emitter = new ChannelNotificationEmitter(
            _inbox, policy, policy == DeliveryPolicy.BufferAlways ? _subscriberId : null);
    }

    public ChannelNotificationEmitter Emitter { get; }

    // The agent coming online mid-test, expressed the way production expresses it: a first poll
    // registers the subscriber, and every emit after it reports delivery.
    public void GoLive() =>
        Collect(_inbox.ReceiveAsync(_subscriberId, TimeSpan.Zero, CancellationToken.None)
            .GetAwaiter().GetResult());

    // A method rather than a property: it drains the inbox, so it is not free and not idempotent
    // to call twice in the same expression.
    public IReadOnlyList<ChannelMessageNotification> Received()
    {
        Collect(_inbox.ReceiveAsync(_subscriberId, TimeSpan.Zero, CancellationToken.None)
            .GetAwaiter().GetResult());
        lock (_gate)
        {
            return _received.ToArray();
        }
    }

    // The cancels the emitter put in the inbox, drained as Received drains messages.
    public IReadOnlyList<ChannelCancelNotification> Cancels()
    {
        Received();
        lock (_gate)
        {
            return _cancels.ToArray();
        }
    }

    public async Task<ChannelMessageNotification> FirstAsync(TimeSpan timeout, CancellationToken ct = default) =>
        (await ReceivedAtLeastAsync(1, timeout, ct))[0];

    // A real long poll against the inbox, so the wait ends when the item actually lands rather
    // than on a sleep.
    public async Task<IReadOnlyList<ChannelMessageNotification>> ReceivedAtLeastAsync(
        int count, TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        while (Received().Count < count && !linked.IsCancellationRequested)
        {
            try
            {
                Collect(await _inbox.ReceiveAsync(
                    _subscriberId, TimeSpan.FromMilliseconds(250), linked.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var received = Received();
        if (received.Count < count)
        {
            throw new TimeoutException(
                $"Expected {count} notification(s) on {_subscriberId} within {timeout}, received {received.Count}.");
        }

        return received;
    }

    private void Collect(IReadOnlyList<ChannelInboxItem> batch)
    {
        lock (_gate)
        {
            _received.AddRange(batch.Where(item => item.Kind == ChannelInboxItemKind.Message).Select(item => item.Message!));
            _cancels.AddRange(batch.Where(item => item.Kind == ChannelInboxItemKind.Cancel).Select(item => item.Cancel!));
        }
    }
}