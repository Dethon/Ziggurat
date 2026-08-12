using System.Collections.Concurrent;
using Domain.Channels;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Channels;

public class ChannelInboxTests
{
    private const string Subscriber = "channel-signalr";

    // Every wait in these tests is bounded so a regression fails the run instead of hanging it:
    // the inbox is driven by a FakeTimeProvider that is never advanced, so a poll that is never
    // signalled would otherwise wait forever.
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(5);

    private static ChannelInboxItem Message(string conversationId) =>
        ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = "user",
            Content = "hello"
        });

    private static ChannelInboxItem Cancel(string conversationId) =>
        ChannelInboxItem.ForCancel(new ChannelCancelNotification { ConversationId = conversationId });

    [Fact]
    public async Task ReceiveAsync_PreservesMessageAndCancelOrdering()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Cancel("c1"));
        inbox.Enqueue(Message("c2"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Select(i => i.Kind).ShouldBe(
            [ChannelInboxItemKind.Message, ChannelInboxItemKind.Cancel, ChannelInboxItemKind.Message]);
    }

    [Fact]
    public async Task Enqueue_BeyondCapacity_DropsOldest()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider(), capacity: 2);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Message("c2"));
        inbox.Enqueue(Message("c3"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Select(i => i.Message!.ConversationId).ShouldBe(["c2", "c3"]);
    }

    // Capacity is the documented survival bound — "capacity, not time" — so crossing it must
    // leave a trace: during a long enough outage user messages start vanishing at this line, and
    // without the warning nothing anywhere records that it happened.
    [Fact]
    public async Task Enqueue_BeyondCapacity_WarnsAboutTheDroppedItem()
    {
        var warnings = new List<string>();
        var inbox = new ChannelInbox(
            new FakeTimeProvider(), capacity: 2, logger: new CapturingLogger(warnings));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Message("c2"));
        warnings.ShouldBeEmpty();

        inbox.Enqueue(Message("c3"));

        warnings.ShouldHaveSingleItem().ShouldContain(Subscriber);
    }

    // The targeted variant exists for channels whose policy is buffer-always (Telegram): a message
    // arriving before the agent's first poll — server cold start, or just after an idle eviction —
    // must land in the well-known subscriber's queue instead of fanning out to nobody.
    [Fact]
    public async Task EnqueueFor_WithNoSubscriberRegistered_CreatesTheQueueAndDeliversOnTheFirstPoll()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());

        inbox.EnqueueFor(Subscriber, Message("c1"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    // The post-eviction reopening of the cold-start window: after the empty subscriber is pruned,
    // a broadcast reaches nobody (see Inbox_AfterEvictingAnIdleSubscriber_...), but the targeted
    // enqueue must mint a fresh queue and buffer.
    [Fact]
    public async Task EnqueueFor_AfterTheSubscriberWasEvicted_StillBuffersForItsNextPoll()
    {
        using var cts = new CancellationTokenSource(_deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        time.Advance(TimeSpan.FromMinutes(6));

        inbox.EnqueueFor(Subscriber, Message("c1"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);
        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    // Creating the queue is not polling it: HasLiveSubscriber answers "has anyone actually polled
    // recently", and a buffer minted by EnqueueFor has no poller yet — reading it as live would
    // resurrect the stale-buffer bug for any caller gating a destructive action on liveness.
    [Fact]
    public async Task EnqueueFor_WithNobodyPolling_DoesNotReadAsALiveSubscriber()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());

        inbox.EnqueueFor(Subscriber, Message("c1"));

        inbox.HasLiveSubscriber().ShouldBeFalse();

        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.HasLiveSubscriber().ShouldBeTrue();
    }

    [Fact]
    public async Task EnqueueFor_WhileAPollIsParked_WakesIt()
    {
        var time = new ArmedClock();
        var inbox = new ChannelInbox(time);
        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);
        // Parking the poll ends with arming its timeout, so that timer is the signal that the
        // waiter this enqueue has to wake is registered. Sleeping instead only made it likely.
        await time.WaitUntilArmedAsync(TimeSpan.FromSeconds(30));

        inbox.EnqueueFor(Subscriber, Message("c1"));

        (await pending.WaitAsync(_deadline)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task EnqueueFor_BeyondCapacity_DropsOldestAndWarns()
    {
        var warnings = new List<string>();
        var inbox = new ChannelInbox(
            new FakeTimeProvider(), capacity: 2, logger: new CapturingLogger(warnings));

        inbox.EnqueueFor(Subscriber, Message("c1"));
        inbox.EnqueueFor(Subscriber, Message("c2"));
        warnings.ShouldBeEmpty();

        inbox.EnqueueFor(Subscriber, Message("c3"));

        warnings.ShouldHaveSingleItem().ShouldContain(Subscriber);
        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        batch.Select(i => i.Message!.ConversationId).ShouldBe(["c2", "c3"]);
    }

    [Fact]
    public async Task ReceiveAsync_WhenEmpty_WakesOnEnqueue()
    {
        var time = new ArmedClock();
        var inbox = new ChannelInbox(time);
        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Wait for the waiter to register before enqueueing.
        await time.WaitUntilArmedAsync(TimeSpan.FromSeconds(30));
        inbox.Enqueue(Message("c1"));

        var batch = await pending.WaitAsync(_deadline);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ReceiveAsync_WhenNothingArrives_ReturnsEmptyAfterTimeout()
    {
        var time = new ArmedClock();
        var inbox = new ChannelInbox(time);
        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        // An advance that lands before the poll arms its timeout fires nothing, and the poll then
        // waits out a clock that has already moved past it — which is a hang, not a failure.
        await time.AdvancePastAsync(TimeSpan.FromSeconds(30));

        (await pending.WaitAsync(_deadline)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_SecondPollForSameSubscriber_DisplacesFirstWithEmptyBatch()
    {
        var time = new ArmedClock();
        var inbox = new ChannelInbox(time);
        var first = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);
        await time.WaitUntilArmedAsync(TimeSpan.FromSeconds(30));

        var second = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        (await first.WaitAsync(_deadline)).ShouldBeEmpty();

        // The second poll parks in its turn, and only then is there a waiter for this to wake.
        await time.WaitUntilArmedAsync(TimeSpan.FromSeconds(30), previously: 1);
        inbox.Enqueue(Message("c1"));
        (await second.WaitAsync(_deadline)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Enqueue_BroadcastsToEverySubscriber()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync("a", TimeSpan.Zero, CancellationToken.None);
        await inbox.ReceiveAsync("b", TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));

        (await inbox.ReceiveAsync("a", TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
        (await inbox.ReceiveAsync("b", TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
    }

    // Eviction is asserted through what it changes rather than through a "do you still have
    // bookkeeping" flag: an evicted subscriber is not in the Enqueue fan-out, so an item published
    // after the timeout reaches nobody and the next poll comes back empty. A subscriber that was
    // not evicted would have buffered it and handed it over here.
    [Fact]
    public async Task Subscriber_WhenIdleAndEmpty_IsEvicted()
    {
        using var cts = new CancellationTokenSource(_deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        // The positive control: while it is registered, the fan-out reaches it.
        inbox.Enqueue(Message("registered"));
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).Count.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(6));
        inbox.Enqueue(Message("after-eviction"));

        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Subscriber_WhenIdleWithQueuedItems_IsNotEvictedAndStillDeliversThem()
    {
        using var cts = new CancellationTokenSource(_deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Message("c2"));

        // A channel outage outlasting the idle timeout must not discard what was buffered during it.
        // Delivering both items below is the survival claim in full — strictly more than asking
        // whether the subscriber is still registered.
        time.Advance(TimeSpan.FromHours(3));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        batch.Select(i => i.Message!.ConversationId).ShouldBe(["c1", "c2"]);
    }

    [Fact]
    public async Task Inbox_AfterEvictingAnIdleSubscriber_StillDeliversToAFreshSubscription()
    {
        using var cts = new CancellationTokenSource(_deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        time.Advance(TimeSpan.FromMinutes(6));

        // Eviction retires the *instance*, so the id must be reusable: a retired subscriber can
        // neither be resurrected by the next poll nor poison its id, and the early return that stops
        // a retired subscriber accepting items must not follow the id to its replacement. The
        // dropped item is what proves the eviction actually happened before the re-subscription —
        // without it, a subscriber that was never evicted would pass this test unchanged.
        inbox.Enqueue(Message("dropped"));
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token)).ShouldBeEmpty();

        inbox.Enqueue(Message("c1"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    // The drain is destructive and a poll never acknowledges what it received, so a batch handed to
    // a request that then dies exists nowhere else. Restore is how the caller that knows its
    // response is dead hands the batch back: at the front, because it is older than anything that
    // arrived while it was away.
    [Fact]
    public async Task Restore_AfterAnAbortedPoll_PutsTheBatchBackAheadOfWhatArrivedMeanwhile()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Message("c2"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        batch.Count.ShouldBe(2);

        // The response died with the batch in it, and a third message landed in the meantime.
        inbox.Enqueue(Message("c3"));
        inbox.Restore(Subscriber, batch);

        var next = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        next.Select(i => i.Message!.ConversationId).ShouldBe(["c1", "c2", "c3"]);
    }

    // The handback answers exactly one case: the drain succeeded and the request died while its
    // response was being built. Nothing else in a poll reaches it — every earlier cancellation check
    // refuses to drain at all — so this is the only test that keeps it from being deleted.
    [Fact]
    public async Task ReceiveAsync_CancelledWhileTheResponseIsBuilt_HandsTheBatchBackAndRethrows()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Cancel("c1"));

        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => inbox.ReceiveAsync(
            Subscriber,
            TimeSpan.Zero,
            batch =>
            {
                // The request hangs up with the batch already out of the queue and nowhere else.
                cts.Cancel();
                return batch.Count;
            },
            cts.Token));

        var next = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        next.Select(item => item.Kind).ShouldBe(
            [ChannelInboxItemKind.Message, ChannelInboxItemKind.Cancel]);
    }

    // The projection is the caller's own work over user-supplied content — in production a
    // JsonSerializer.Serialize — so it can throw on one bad message. The drain already emptied the
    // queue, so without a handback that one message takes the whole pending batch, cancels included,
    // with it. The poll still fails; what it must not do is destroy what it was carrying.
    [Fact]
    public async Task ReceiveAsync_WhenTheProjectionThrows_KeepsTheBatchForTheNextPoll()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Cancel("c1"));

        await Should.ThrowAsync<InvalidOperationException>(() => inbox.ReceiveAsync<int>(
            Subscriber,
            TimeSpan.Zero,
            _ => throw new InvalidOperationException("bad content"),
            CancellationToken.None));

        var next = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        next.Select(item => item.Kind).ShouldBe(
            [ChannelInboxItemKind.Message, ChannelInboxItemKind.Cancel]);
    }

    // Putting the batch back is not a step of its own: between the drain and the handback the queue
    // looks empty, so a poll racing in there takes whatever arrived after the batch and dispatches
    // it, and the older items only reappear behind it — the agent seeing a message before the cancel
    // that preceded it. Draining, turning the batch into the caller's response and putting it back
    // are one turn at the queue, and no other poll on this subscriber gets between them.
    [Fact]
    public async Task ReceiveAsync_WhenADeadPollHandsItsBatchBack_ARacingPollCannotTakeNewerItemsFirst()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        inbox.Enqueue(Message("first"));

        using var cts = new CancellationTokenSource();
        using var racingStarted = new ManualResetEventSlim();
        Task<IReadOnlyList<ChannelInboxItem>>? racing = null;

        var aborted = inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, batch =>
        {
            // The response is being written when a newer message lands and the reconnect's fresh
            // poll arrives — both while this batch is still in hand.
            inbox.Enqueue(Message("second"));
            racing = Task.Run(() =>
            {
                racingStarted.Set();
                return inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
            });
            racingStarted.Wait(_deadline);
            Thread.Sleep(100);
            cts.Cancel();
            return batch;
        }, cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => aborted);

        // Everything the subscriber is ever handed, in the order it is handed over.
        var delivered = (await racing!.WaitAsync(_deadline))
            .Concat(await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None))
            .Select(item => item.Message!.ConversationId);

        delivered.ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task Restore_WhileTheNextPollIsAlreadyParked_WakesIt()
    {
        var time = new ArmedClock();
        var inbox = new ChannelInbox(time);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        inbox.Enqueue(Message("c1"));
        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);
        await time.WaitUntilArmedAsync(TimeSpan.FromSeconds(30));

        inbox.Restore(Subscriber, batch);

        (await pending.WaitAsync(_deadline)).Count.ShouldBe(1);
    }

    // A restored batch is subject to the same single bound as everything else, and crossing it is
    // still the moment a message is lost, so it still warns.
    [Fact]
    public async Task Restore_BeyondCapacity_DropsOldestAndWarns()
    {
        var warnings = new List<string>();
        var inbox = new ChannelInbox(
            new FakeTimeProvider(), capacity: 2, logger: new CapturingLogger(warnings));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        inbox.Enqueue(Message("c1"));
        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c2"));
        inbox.Enqueue(Message("c3"));
        warnings.ShouldBeEmpty();

        inbox.Restore(Subscriber, batch);

        warnings.ShouldHaveSingleItem().ShouldContain(Subscriber);
        var next = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        next.Select(i => i.Message!.ConversationId).ShouldBe(["c2", "c3"]);
    }

    [Fact]
    public async Task Restore_ForASubscriberThatWasEvicted_MintsTheQueueAgain()
    {
        using var cts = new CancellationTokenSource(_deadline);
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);
        inbox.Enqueue(Message("c1"));
        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token);

        time.Advance(TimeSpan.FromMinutes(6));

        inbox.Restore(Subscriber, batch);

        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, cts.Token))
            .Select(i => i.Message!.ConversationId).ShouldBe(["c1"]);
    }

    // A subscriber is stamped when its poll *starts*, so the longest legitimate quiet gap is a
    // fully held poll followed by one worst-case retry backoff (the pump's ceiling after a failed
    // call), plus network and scheduling slop. Freshness must cover that whole gap: a value that
    // only covers the held poll reads a healthy-but-flaky pump as dead, and ServiceBus punishes
    // that by abandoning deliveries — ticking the message's delivery count toward the DLQ cap for
    // an agent that is actually connected.
    [Fact]
    public async Task HasLiveSubscriber_AfterAHeldPollAndOneRetryBackoff_IsStillTrue()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        time.Advance(
            TimeSpan.FromMilliseconds(
                ChannelProtocol.DefaultReceiveWaitMs + ChannelProtocol.MaxReceiveRetryBackoffMs)
            + TimeSpan.FromSeconds(5));

        inbox.HasLiveSubscriber().ShouldBeTrue();
    }

    // The exact regression this method exists to close: PruneIdle keeps a subscriber that is
    // holding items alive for up to an hour so a channel outage survives, but that must not be
    // mistaken for "someone is listening" — a caller gating a destructive action (deleting a
    // schedule, dropping a routing entry) on liveness needs this to read false once nobody has
    // actually polled in a while, even though the queue is non-empty.
    [Fact]
    public async Task HasLiveSubscriber_SubscriberHoldingItemsButNotPolling_IsFalse()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromHours(1));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        time.Advance(ChannelInbox._liveSubscriberFreshness + TimeSpan.FromSeconds(1));

        inbox.HasLiveSubscriber().ShouldBeFalse();

        // ...while the subscriber itself is very much still there: the buffered item is delivered
        // on the next poll. "Not live" must not be read as "evicted" — the two answers are
        // deliberately different, and this is the case that separates them.
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReceiveAsync_WhenAnotherPollTakesTheBatchFirst_ReturnsEmptyWithoutLosingItems()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var context = new ManualSynchronizationContext();
        var parked = context.Start(() =>
            inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None));

        inbox.Enqueue(Message("c1"));

        // The parked poll has been signalled but cannot resume until pumped, so the second poll
        // takes the batch. The item must land in exactly one of them, never be lost between them.
        var taken = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        taken.Count.ShouldBe(1);
        taken[0].Message!.ConversationId.ShouldBe("c1");
        (await context.PumpUntilAsync(parked, _deadline)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_WhenCancelled_LeavesItemsForTheNextPoll()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        var context = new ManualSynchronizationContext();
        var parked = context.Start(() => inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), cts.Token));

        inbox.Enqueue(Message("c1"));
        await cts.CancelAsync();

        // The caller is gone, so the batch must stay queued rather than be handed to an aborted poll.
        await Should.ThrowAsync<OperationCanceledException>(() => context.PumpUntilAsync(parked, _deadline));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    // The sibling of ReceiveAsync_WhenCancelled_LeavesItemsForTheNextPoll, on the path that has no
    // wait at all. ReconnectAsync aborts a poll and starts a fresh one immediately behind it, so a
    // token that is already cancelled when the poll arrives is the common case, not a corner: the
    // pending-items fast path must refuse it exactly like the wait path does, or the batch is
    // drained into a caller that is already gone.
    [Fact]
    public async Task ReceiveAsync_WhenAlreadyCancelled_LeavesPendingItemsQueued()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), cts.Token));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ReceiveAsync_WhenAResumingPollFinishes_DoesNotOrphanALaterWaiter()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var context = new ManualSynchronizationContext();
        var parked = context.Start(() =>
            inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None));

        inbox.Enqueue(Message("c1"));

        // Take everything the parked poll was signalled for, then register a fresh waiter while it
        // is still suspended. Retiring the parked poll must not null out this later waiter.
        (await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
        var next = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        (await context.PumpUntilAsync(parked, _deadline)).ShouldBeEmpty();

        inbox.Enqueue(Message("c2"));

        var batch = await next.WaitAsync(_deadline);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c2");
    }

    private sealed class CapturingLogger(List<string> warnings) : ILogger<ChannelInbox>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }

    // Parks a poll's continuations on a context only the test pumps, which pins the interleaving
    // the racy sites need without adding a seam to production code. It works because the inbox
    // awaits without ConfigureAwait(false), so the continuation is posted to the captured context;
    // adding ConfigureAwait(false) there would not fail these tests, it would quietly make them
    // race again.
    private sealed class ManualSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public T Start<T>(Func<T> work)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return work();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public async Task<T> PumpUntilAsync<T>(Task<T> task, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (!task.IsCompleted)
            {
                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("The parked poll never completed.");
                }

                RunPending();
                await Task.Delay(5);
            }

            return await task;
        }

        private void RunPending()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_pending.TryDequeue(out var work))
                {
                    work.Callback(work.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}