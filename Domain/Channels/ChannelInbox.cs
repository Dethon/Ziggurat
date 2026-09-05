using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace Domain.Channels;

// What a broadcast fan-out did: `Accepted` is "some subscriber holds a copy", `Live` is "one of
// them was polling as it landed". Live implies Accepted; the gap between them is a quiet
// subscriber whose copy waits for its next poll.
public readonly record struct EnqueueReceipt(bool Accepted, bool Live);

public sealed class ChannelInbox(
    TimeProvider? timeProvider = null,
    int capacity = 256,
    TimeSpan? subscriberIdleTimeout = null,
    ILogger<ChannelInbox>? logger = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // Only an *empty* subscriber is ever evicted, so this bounds nothing but abandoned bookkeeping.
    // A healthy agent touches its subscriber at least every ~60s (a fully held 30s poll plus the
    // 30s retry backoff ceiling), so an hour is ~60x any legitimate gap.
    private readonly TimeSpan _idleTimeout = subscriberIdleTimeout ?? TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();
    private readonly int _capacity = ValidateCapacity(capacity);

    // How long a subscriber still counts as "someone is actually listening" after its last poll.
    // Sized to the worst legitimate quiet gap rather than a round number: a subscriber is stamped
    // when its poll *starts*, so a healthy pump can go a fully held poll plus one failed call's
    // worst-case backoff between touches, and the margin absorbs network and scheduling slop past
    // that boundary. Internal, and not a parameter: six emitters passing their own value is what
    // let six near-miss variants exist and be fixed three separate times.
    internal static readonly TimeSpan _liveSubscriberFreshness = TimeSpan.FromMilliseconds(
        ChannelProtocol.DefaultReceiveWaitMs + ChannelProtocol.MaxReceiveRetryBackoffMs + 15_000);

    // The liveness rule, and the only one: "is there bookkeeping for this id" stays true for up to
    // an hour after a subscriber goes quiet — precisely so a channel outage doesn't discard what was
    // buffered during it (see PruneIdle) — which makes it the wrong question for a caller about to
    // act on "delivery". This one asks whether *someone actually polled recently*; a subscriber
    // holding items but not repolling does not count.
    //
    // Not the question a delivering caller asks: an answer taken before an enqueue is about a moment
    // that has passed by the time the item lands, so the enqueues below answer it themselves and
    // this stays here for the rule's own tests.
    internal bool HasLiveSubscriber()
    {
        PruneIdle();
        var cutoff = _timeProvider.GetUtcNow() - _liveSubscriberFreshness;
        return _subscribers.Values.Any(subscriber => subscriber.IsLiveSince(cutoff));
    }

    // Fans out to every subscriber whatever its freshness, and answers whether any of them was live
    // as its copy went in.
    public bool Enqueue(ChannelInboxItem item) => FanOut(item, onlyIfLive: false).Live;

    // The same fan-out, restricted to subscribers that are live as the item lands: for a caller that
    // settles a durable record on the answer, so an item must never sit in a buffer the "yes" it was
    // given claims someone is draining.
    public bool EnqueueIfLive(ChannelInboxItem item) => FanOut(item, onlyIfLive: true).Live;

    // The broadcast fan-out with both of its answers: whether any subscriber took the item at all,
    // and whether one of them was live. They differ exactly for a registered subscriber that has gone
    // quiet — the agent mid-reconnect — whose copy is buffered and delivered on its next poll. A
    // caller that answers an outside party (the watch callback answering Home Assistant) reports
    // that as accepted rather than lost; liveness stays the answer for the callers that settle a
    // durable record on it.
    public EnqueueReceipt EnqueueWithReceipt(ChannelInboxItem item) => FanOut(item, onlyIfLive: false);

    private EnqueueReceipt FanOut(ChannelInboxItem item, bool onlyIfLive)
    {
        PruneIdle();
        var cutoff = _timeProvider.GetUtcNow() - _liveSubscriberFreshness;
        var results = _subscribers
            .Select(entry => (entry.Key, Result: entry.Value.Enqueue(item, _capacity, cutoff, onlyIfLive)))
            .ToArray();
        WarnDroppedOldest(results
            .Where(entry => entry.Result.Outcome == EnqueueOutcome.AcceptedDroppingOldest)
            .Select(entry => entry.Key)
            .ToArray());
        return new EnqueueReceipt(
            results.Any(entry => entry.Result.Outcome is EnqueueOutcome.Accepted or EnqueueOutcome.AcceptedDroppingOldest),
            results.Any(entry => entry.Result.Live));
    }

    // The targeted variant, for channels whose delivery policy is buffer-always (Telegram): unlike
    // the broadcast Enqueue it creates the subscriber's queue on demand, so a message arriving
    // before the agent's first poll — server cold start, or just after an idle eviction — is
    // buffered instead of fanned out to nobody. Creation is bookkeeping only: the subscriber does
    // not count as live until someone actually polls it, and capacity remains the only bound.
    //
    // Liveness comes back from the enqueue for the same reason as above, and it is about this id:
    // "is anyone live" would read a poller under a different derived id as delivery, and the caller's
    // not-live warning would stay silent while items pile into a queue nobody drains.
    public bool EnqueueFor(string subscriberId, ChannelInboxItem item)
    {
        PruneIdle();
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - _liveSubscriberFreshness;
        while (true)
        {
            // Same seeding rationale as ReceiveAsync: left at default, the stamp would sit behind
            // every cutoff and a concurrent prune could retire and remove the instance this call
            // just created before the item lands in it.
            var subscriber = _subscribers.GetOrAdd(subscriberId, _ => new Subscriber(now));
            var (outcome, live) = subscriber.Enqueue(item, _capacity, cutoff, onlyIfLive: false);
            if (outcome != EnqueueOutcome.Refused)
            {
                if (outcome == EnqueueOutcome.AcceptedDroppingOldest)
                {
                    WarnDroppedOldest([subscriberId]);
                }

                return live;
            }

            // A concurrent prune retired this instance between the lookup and the enqueue; finish
            // its removal rather than hand the item to a queue nobody drains. Each pass drops the
            // instance it observed, so the loop is bounded.
            _subscribers.TryRemove(new KeyValuePair<string, Subscriber>(subscriberId, subscriber));
        }
    }

    // The other half of a drain. A poll never acknowledges what it received, so a batch handed to a
    // request that then dies exists nowhere else — the drain already emptied the queue. A poll that
    // finds its response dead hands the batch back here and it goes in at the front, because it is
    // older than anything that arrived while it was away. Order across the whole queue is preserved,
    // which is what the message/cancel sequence depends on.
    //
    // Not a step a caller performs on its own: it is the tail of ReceiveAsync's own turn at the
    // queue, taken while that turn still holds the subscriber, so nothing can drain in between.
    internal void Restore(string subscriberId, IReadOnlyList<ChannelInboxItem> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        // No prune here: this call exists to keep items, and the subscriber it restores to is the
        // one the batch was drained from.
        var now = _timeProvider.GetUtcNow();
        while (true)
        {
            var subscriber = _subscribers.GetOrAdd(subscriberId, _ => new Subscriber(now));
            var outcome = subscriber.Restore(batch, _capacity);
            if (outcome != EnqueueOutcome.Refused)
            {
                if (outcome == EnqueueOutcome.AcceptedDroppingOldest)
                {
                    WarnDroppedOldest([subscriberId]);
                }

                return;
            }

            // Same retirement race as EnqueueFor: a prune retired this instance between the lookup
            // and the restore, so finish its removal and put the batch into its replacement.
            _subscribers.TryRemove(new KeyValuePair<string, Subscriber>(subscriberId, subscriber));
        }
    }

    private void WarnDroppedOldest(IReadOnlyList<string> subscriberIds)
    {
        if (subscriberIds.Count > 0)
        {
            // Capacity is the only bound on what an outage can buffer, so crossing it is the
            // moment a message is irrecoverably lost — the one line that must not pass silently.
            logger?.LogWarning(
                "Inbox at capacity ({Capacity}); dropped the oldest buffered item for {SubscriberIds}",
                _capacity, string.Join(", ", subscriberIds));
        }
    }

    // A poll's whole turn at the queue, in one call: the batch is drained, turned into whatever the
    // caller sends back, and — if that caller is already gone — put back at the front, with no other
    // poll on this subscriber getting in between. The three are one operation because the middle one
    // decides whether the first one counts: a poll never acknowledges what it received, so a batch
    // that was drained into a dead response exists nowhere else, and handing it back as a separate
    // step let a racing poll take a message that arrived after it and dispatch it first.
    public Task<T> ReceiveAsync<T>(
        string subscriberId,
        TimeSpan maxWait,
        Func<IReadOnlyList<ChannelInboxItem>, T> project,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var now = _timeProvider.GetUtcNow();
        while (true)
        {
            // Seeding the new subscriber with `now` matters: left at default it would sit behind
            // every cutoff, so a concurrent Enqueue's prune could retire and remove it in the window
            // before TryTouch and then broadcast to a snapshot that excludes it — dropping the item.
            var subscriber = _subscribers.GetOrAdd(subscriberId, _ => new Subscriber(now));
            if (subscriber.TryTouch(now))
            {
                return subscriber.ReceiveAsync(
                    maxWait, _timeProvider, project, batch => Restore(subscriberId, batch), ct);
            }

            // A concurrent prune retired this instance between the lookup and the touch. Seeding
            // makes that unreachable for a subscriber this call created, so only one already past
            // the cutoff can land here; finish its removal rather than poll a subscriber Enqueue can
            // no longer reach. Each pass drops the instance it observed, so the loop is bounded.
            _subscribers.TryRemove(new KeyValuePair<string, Subscriber>(subscriberId, subscriber));
        }
    }

    // The batch itself, for a caller with nowhere to hand it back to: it is committed the moment
    // this returns.
    public Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(
        string subscriberId,
        TimeSpan maxWait,
        CancellationToken ct) =>
        ReceiveAsync(subscriberId, maxWait, static batch => batch, ct);

    private static int ValidateCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return capacity;
    }

    // Eviction exists only to stop the subscriber map growing without bound; it must never be the
    // thing that loses a message. A subscriber still holding items is kept however long it has been
    // idle, so a channel outage of any length is survivable — capacity, not time, is the bound.
    private void PruneIdle()
    {
        var cutoff = _timeProvider.GetUtcNow() - _idleTimeout;
        var retired = _subscribers.Where(kv => kv.Value.TryRetire(cutoff)).ToArray();
        foreach (var entry in retired)
        {
            _subscribers.TryRemove(entry);
        }
    }

    private enum EnqueueOutcome
    {
        // The retirement latch refused the item; the caller must not treat it as buffered.
        Refused,
        // The subscriber was not live and the caller asked for live subscribers only.
        NotLive,
        Accepted,
        AcceptedDroppingOldest
    }

    private sealed class Subscriber(DateTimeOffset createdAt)
    {
        private readonly Lock _gate = new();
        // Held for a poll's turn at the queue — drain, project, hand back — and never while it is
        // parked waiting. Separate from _gate on purpose: producers must not queue behind a caller
        // building its response, and only drains have to wait for each other.
        private readonly Lock _handoff = new();
        private readonly Queue<ChannelInboxItem> _items = new();
        private TaskCompletionSource<bool>? _waiter;
        private DateTimeOffset _lastPolledAt = createdAt;
        private bool _hasPolled;
        private bool _retired;

        public bool TryTouch(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _lastPolledAt = now;
                _hasPolled = true;
                return true;
            }
        }

        // Deliberately ignores _items: a subscriber that is only holding buffered items but hasn't
        // repolled since the cutoff is exactly the stale-buffer case HasLiveSubscriber must reject.
        // Requiring an actual poll matters for the same reason: a queue minted by EnqueueFor has a
        // fresh seed stamp but no poller yet, and reading it as live would hand the stale-buffer
        // bug right back to any caller gating a destructive action on liveness.
        public bool IsLiveSince(DateTimeOffset cutoff)
        {
            lock (_gate)
            {
                return !_retired && _hasPolled && _lastPolledAt >= cutoff;
            }
        }

        // Both conditions are read under the same lock that Enqueue and Drain mutate them under, and
        // the retirement is latched in the same critical section: an item can never be accepted into
        // a queue that a prune has already decided to throw away.
        //
        // A subscriber is stamped when its poll *starts*, so "a touch protects for one idle timeout"
        // only holds while the caller's maxWait stays below that timeout — otherwise a poll could be
        // retired while still parked on it, and items arriving before it re-polls would reach no
        // subscriber. maxWaitMs is caller-supplied (ChannelReceiveTool), 30s against a 1h timeout
        // today, so there is decades of headroom; a caller closing that gap would have to widen the
        // timeout to match.
        public bool TryRetire(DateTimeOffset cutoff)
        {
            lock (_gate)
            {
                if (_retired || _items.Count > 0 || _lastPolledAt >= cutoff)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }

        // Liveness is read in the critical section that accepts the item, so the answer is about
        // this item and not about a moment before it: a subscriber cannot be live to the question
        // and gone by the time the item lands, which is what let a caller settle a durable record
        // against an item sitting in a buffer nobody drains.
        public (EnqueueOutcome Outcome, bool Live) Enqueue(
            ChannelInboxItem item, int capacity, DateTimeOffset liveCutoff, bool onlyIfLive)
        {
            TaskCompletionSource<bool>? toSignal;
            var droppedOldest = false;
            bool live;
            lock (_gate)
            {
                // Refusing here is what makes retirement safe: a prune that has already latched this
                // subscriber must not be handed an item by a thread whose _subscribers snapshot
                // predates the removal, or that item would be accepted into a queue nobody drains.
                if (_retired)
                {
                    return (EnqueueOutcome.Refused, false);
                }

                live = _hasPolled && _lastPolledAt >= liveCutoff;
                if (onlyIfLive && !live)
                {
                    return (EnqueueOutcome.NotLive, false);
                }

                if (_items.Count >= capacity)
                {
                    _items.Dequeue();
                    droppedOldest = true;
                }

                _items.Enqueue(item);
                toSignal = _waiter;
                _waiter = null;
            }

            toSignal?.TrySetResult(true);
            return (droppedOldest ? EnqueueOutcome.AcceptedDroppingOldest : EnqueueOutcome.Accepted, live);
        }

        // A batch coming back from a poll whose response died. It goes ahead of whatever arrived
        // while it was away, and over capacity the oldest still goes — the same rule Enqueue
        // applies, so a restore cannot grow the queue past its one bound.
        public EnqueueOutcome Restore(IReadOnlyList<ChannelInboxItem> batch, int capacity)
        {
            TaskCompletionSource<bool>? toSignal;
            int dropped;
            lock (_gate)
            {
                if (_retired)
                {
                    return EnqueueOutcome.Refused;
                }

                var rebuilt = batch.Concat(_items).ToArray();
                dropped = Math.Max(0, rebuilt.Length - capacity);
                _items.Clear();
                foreach (var item in rebuilt.Skip(dropped))
                {
                    _items.Enqueue(item);
                }

                toSignal = _waiter;
                _waiter = null;
            }

            // A poll may already be parked on the empty queue the aborted one left behind; without
            // this it would sleep out its whole wait with the restored batch sitting in front of it.
            toSignal?.TrySetResult(true);
            return dropped > 0 ? EnqueueOutcome.AcceptedDroppingOldest : EnqueueOutcome.Accepted;
        }

        public async Task<T> ReceiveAsync<T>(
            TimeSpan maxWait,
            TimeProvider timeProvider,
            Func<IReadOnlyList<ChannelInboxItem>, T> project,
            Action<IReadOnlyList<ChannelInboxItem>> handBack,
            CancellationToken ct)
        {
            // Same contract as the post-wait check below, on the path that never waits: a poll whose
            // caller has already hung up must not drain. ReconnectAsync aborts a poll and issues a
            // fresh one immediately behind it, so an already-cancelled token reaching the fast path
            // is routine — and draining there hands the batch to a dead request and loses it.
            ct.ThrowIfCancellationRequested();

            TaskCompletionSource<bool>? waiter = null;
            TaskCompletionSource<bool>? displaced = null;
            lock (_handoff)
            {
                IReadOnlyList<ChannelInboxItem>? pending = null;
                lock (_gate)
                {
                    if (_items.Count > 0)
                    {
                        pending = Drain(ct);
                    }
                    else
                    {
                        // Deciding to wait and registering the waiter stay one step: an item landing
                        // between them would find no waiter to signal, and this poll would sleep out
                        // its whole maxWait with the item already in its queue.
                        displaced = _waiter;
                        waiter = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        _waiter = waiter;
                    }
                }

                if (pending is not null)
                {
                    return Complete(pending, project, handBack, ct);
                }
            }

            // A second poll for the same subscriber retires the first with an empty batch,
            // otherwise two waiters would split the stream between them.
            displaced?.TrySetResult(false);

            if (maxWait <= TimeSpan.Zero)
            {
                return RetireAndComplete(waiter!, project, handBack, ct);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(maxWait, timeProvider, timeoutCts.Token);
            var completed = await Task.WhenAny(waiter!.Task, delay);
            await timeoutCts.CancelAsync();

            if (ct.IsCancellationRequested)
            {
                // The caller is gone — draining here would hand the batch to an aborted request and
                // lose it. Leave the items queued for the next poll and surface the cancellation.
                lock (_gate)
                {
                    RetireWaiter(waiter);
                }

                ct.ThrowIfCancellationRequested();
            }

            if (completed == waiter.Task && !waiter.Task.Result)
            {
                return project([]);
            }

            return RetireAndComplete(waiter, project, handBack, ct);
        }

        private T RetireAndComplete<T>(
            TaskCompletionSource<bool> waiter,
            Func<IReadOnlyList<ChannelInboxItem>, T> project,
            Action<IReadOnlyList<ChannelInboxItem>> handBack,
            CancellationToken ct)
        {
            lock (_handoff)
            {
                IReadOnlyList<ChannelInboxItem> batch;
                lock (_gate)
                {
                    RetireWaiter(waiter);
                    batch = Drain(ct);
                }

                return Complete(batch, project, handBack, ct);
            }
        }

        // Caller holds _handoff and nothing else: the projection is the caller's own work — building
        // the response — so it must not run under the lock producers enqueue against.
        //
        // The drain emptied the queue and a poll acknowledges nothing, so from here the batch exists
        // only in the response being built: a request aborted while it is being written takes the
        // batch with it. Asking the token once more with the response in hand covers everything up
        // to the write, and a batch that has nowhere to go goes back to the front of the queue. The
        // write itself stays open — closing it needs an acknowledgement this protocol does not have.
        //
        // A projection that throws is the same loss by another route: it is the caller's own work
        // over content the sender chose, so one unserializable message would otherwise take the
        // whole batch — cancels included — with it. The poll still fails; the batch goes back.
        private static T Complete<T>(
            IReadOnlyList<ChannelInboxItem> batch,
            Func<IReadOnlyList<ChannelInboxItem>, T> project,
            Action<IReadOnlyList<ChannelInboxItem>> handBack,
            CancellationToken ct)
        {
            T projected;
            try
            {
                projected = project(batch);
            }
            catch
            {
                if (batch.Count > 0)
                {
                    handBack(batch);
                }

                throw;
            }

            if (batch.Count > 0 && ct.IsCancellationRequested)
            {
                handBack(batch);
                ct.ThrowIfCancellationRequested();
            }

            return projected;
        }

        // Caller holds _gate. Only the poll that registered a waiter may retire it: a poll that
        // blindly nulls _waiter can drop the waiter a *later* poll registered while this one was
        // resuming, leaving the next Enqueue with nobody to signal — that poll then sleeps out its
        // whole maxWait with items already sitting in its queue.
        private void RetireWaiter(TaskCompletionSource<bool> waiter)
        {
            if (ReferenceEquals(_waiter, waiter))
            {
                _waiter = null;
            }
        }

        // Caller holds _gate. The token is asked one last time with the batch already in hand: a
        // request that hung up between the wait ending and this line would take the items into a
        // response nobody reads, and nothing here is acknowledged, so they would be gone. Asking
        // before the queue is emptied means there is nothing to put back.
        //
        // What is left of the window is the response itself, which Complete covers: it asks once
        // more with the response built and hands the batch back. Only the write is left open, and
        // closing that needs an acknowledgement from the poller this protocol does not have.
        private IReadOnlyList<ChannelInboxItem> Drain(CancellationToken ct)
        {
            var drained = _items.ToArray();
            ct.ThrowIfCancellationRequested();
            _items.Clear();
            return drained;
        }
    }
}