using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class WakeArbiterTests
{
    private sealed class ListPublisher : IMetricsPublisher
    {
        public readonly List<MetricEvent> Events = [];
        public void Publish(MetricEvent evt)
        {
            lock (Events)
            { Events.Add(evt); }
        }
    }

    private sealed class SatelliteHarness
    {
        public readonly SatelliteSession Session;
        public int Paused;
        public int LegacyEnded;
        public UtteranceCapture? Capture;
        // Stands in for a half-open satellite socket: WyomingWriter.WriteAsync throws IOException.
        public bool FailPause;
        // Stands in for the worse half-open socket: the write neither completes nor throws, which
        // is what a TCP send into a black hole actually does. PauseEntered lets a test wait for the
        // arbiter to actually be parked on it before advancing the clock onto the re-arm deadline.
        public bool HangPause;
        public readonly TaskCompletionSource PauseEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SatelliteHarness(string id, string room, double offsetDb = 0)
        {
            Session = new SatelliteSession(id, new SatelliteConfig
            {
                Identity = "household", Room = room, RmsOffsetDb = offsetDb
            });
        }

        public WakeArbiterHandle Handle => new(
            SatelliteIdentity.Of(Session),
            Session.Config.RmsOffsetDb,
            () => Session.SupportsPause,
            () => Session.Mic.Activity,
            () => Session.Mic.TryAbort(),
            _ =>
            {
                if (FailPause)
                {
                    return Task.FromException(new IOException("satellite socket is dead"));
                }
                if (HangPause)
                {
                    PauseEntered.TrySetResult();
                    return new TaskCompletionSource().Task;
                }
                Interlocked.Increment(ref Paused);
                return Task.CompletedTask;
            },
            _ => { Interlocked.Increment(ref LegacyEnded); return Task.CompletedTask; });

        public void OpenCapture(ArmedClock time, ArbitrationSettings settings)
        {
            var gate = new SilenceGate(
                new AdaptiveLevelTracker(500, 9, 4, 15, TimeSpan.FromSeconds(3)),
                TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200));
            Capture = Session.Mic.Open(gate, new ChunkHistory(time, settings.HistorySpan));
        }
    }

    private static (WakeArbiter Arbiter, ArmedClock Time, ListPublisher Metrics,
        VoiceConversationManager Conversations) Create(ArbitrationSettings? settings = null)
    {
        var time = new ArmedClock(DateTimeOffset.UtcNow);
        var metrics = new ListPublisher();
        var conversations = TestConversationManager(time);
        var arbiter = new WakeArbiter(
            settings ?? new ArbitrationSettings(), conversations, metrics, time,
            NullLogger<WakeArbiter>.Instance);
        return (arbiter, time, metrics, conversations);
    }

    // The exact IConversationFactory fake + ReplyTextAccumulator construction from
    // VoiceConversationManagerTests.Build, so handoff runs against the real manager.
    private static VoiceConversationManager TestConversationManager(ArmedClock clock)
    {
        var factory = new Mock<IConversationFactory>();
        var counter = 0;
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                counter++;
                var topicId = $"topic-{counter}";
                var identity = ConversationIdGenerator.CreateFor(topicId);
                var topic = new TopicMetadata(topicId, identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", clock.GetUtcNow(), null);
                return new ConversationCreation(identity, topic);
            });

        return new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), clock, TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance);
    }

    // The decision runs on its own task, so anything observed after a clock advance has to be
    // polled for rather than assumed to have happened within a fixed real-time sleep.
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        condition().ShouldBeTrue(because);
    }

    // Let DecideAfterWindowAsync reach its Task.Delay, fire it, then let the decision run.
    //
    // Both halves used to be fixed sleeps and both were guesses. The first guessed that the window
    // delay had been armed, and an advance that lands before it fires nothing at all — the decision
    // then never runs and the failure names whatever the decision was supposed to have done. The
    // window is the arbiter's own span, so waiting for that wait to be outstanding says it exactly.
    //
    // The tail is the decision pausing its losers on its own task, and fifty milliseconds was a
    // guess at how long that takes. It held while the suite ran on twelve threads and stopped
    // holding on twenty-four, where a run failed on a pause that had simply not been written yet. A
    // caller that can state what it is waiting for passes it and waits for the thing itself.
    private static async Task SettleAsync(ArmedClock time, int windowMs, Func<bool>? until = null)
    {
        await time.AdvancePastLiveAsync(
            TimeSpan.FromMilliseconds(windowMs), TimeSpan.FromMilliseconds(windowMs + 1));

        if (until is null)
        {
            await Eventually.Settle();
            return;
        }

        await WaitUntilAsync(until, "the decision did not settle within its deadline");
    }

    [Fact]
    public async Task Claim_TwoCoincidentWakes_LouderWinsQuieterIsPausedWithoutDispatch()
    {
        var (arbiter, time, metrics, _) = Create();
        var near = new SatelliteHarness("near", "Office A");
        var far = new SatelliteHarness("far", "Office B");
        arbiter.Register("near", near.Handle);
        arbiter.Register("far", far.Handle);
        near.Session.MarkSupportsPause();
        far.Session.MarkSupportsPause();
        near.OpenCapture(time, new ArbitrationSettings());
        far.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("far", 200, 0.8, "wake");
        arbiter.Claim("near", 900, 0.9, "wake");
        await SettleAsync(time, 500);

        far.Paused.ShouldBe(1);
        near.Paused.ShouldBe(0);
        far.Capture!.Completed.IsCompleted.ShouldBeTrue();
        (await far.Capture.Completed).ShouldBe(CaptureOutcome.Abandoned);
        near.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed).SatelliteId.ShouldBe("far");
    }

    [Fact]
    public async Task Claim_ArbitrationDisabled_LeavesBothSatellitesUntouched()
    {
        // Arbitration__Enabled=false is the documented rollback lever for every stage of the
        // rollout: with it off, two coincident wakes must behave exactly as they did before the
        // feature existed — both captures live, both satellites answering, nothing recorded.
        var settings = new ArbitrationSettings { Enabled = false };
        var (arbiter, time, metrics, _) = Create(settings);
        var near = new SatelliteHarness("near", "Office A");
        var far = new SatelliteHarness("far", "Office B");
        arbiter.Register("near", near.Handle);
        arbiter.Register("far", far.Handle);
        near.Session.MarkSupportsPause();
        far.Session.MarkSupportsPause();
        near.OpenCapture(time, settings);
        far.OpenCapture(time, settings);

        arbiter.Claim("far", 200, 0.8, "wake");
        arbiter.Claim("near", 900, 0.9, "wake");

        // Arbitration is off, so no coincidence window is ever opened and there is no wait for
        // SettleAsync to advance past. What is claimed below is an absence, and only time buys one.
        await Eventually.Settle();

        near.Paused.ShouldBe(0);
        far.Paused.ShouldBe(0);
        near.LegacyEnded.ShouldBe(0);
        far.LegacyEnded.ShouldBe(0);
        near.Capture!.Completed.IsCompleted.ShouldBeFalse();
        far.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_RmsOffsetCalibration_FlipsTheWinner()
    {
        var (arbiter, time, _, _) = Create();
        var hot = new SatelliteHarness("hot-mic", "A");             // louder raw, no offset
        var calibrated = new SatelliteHarness("quiet-mic", "B", offsetDb: 12); // +12 dB ~= x3.98
        arbiter.Register("hot-mic", hot.Handle);
        arbiter.Register("quiet-mic", calibrated.Handle);
        hot.Session.MarkSupportsPause();
        calibrated.Session.MarkSupportsPause();
        hot.OpenCapture(time, new ArbitrationSettings());
        calibrated.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("hot-mic", 300, null, "wake");
        arbiter.Claim("quiet-mic", 100, null, "wake"); // 100 * 3.98 = 398 > 300
        await SettleAsync(time, 500);

        hot.Paused.ShouldBe(1);
        calibrated.Paused.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_SingleRegisteredSatellite_IsANoOp()
    {
        var (arbiter, time, metrics, _) = Create();
        var only = new SatelliteHarness("only", "A");
        arbiter.Register("only", only.Handle);
        only.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("only", 500, null, "wake");

        // One satellite is not a contest, so the arbiter returns without opening a window.
        await Eventually.Settle();

        only.Paused.ShouldBe(0);
        only.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_LegacyLoserWithoutRms_GetsTranscriptFallback()
    {
        var (arbiter, time, _, _) = Create();
        var legacy = new SatelliteHarness("legacy", "A"); // never MarkSupportsPause
        var modern = new SatelliteHarness("modern", "B");
        arbiter.Register("legacy", legacy.Handle);
        arbiter.Register("modern", modern.Handle);
        modern.Session.MarkSupportsPause();
        legacy.OpenCapture(time, new ArbitrationSettings());
        modern.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("legacy", null, null, "wake");
        arbiter.Claim("modern", 400, null, "wake");
        await SettleAsync(time, 500);

        legacy.LegacyEnded.ShouldBe(1);
        legacy.Paused.ShouldBe(0);
    }

    [Fact]
    public async Task Claim_FreshWakeVsQuietOpenHolder_BothProceed()
    {
        // Holder's open capture heard nothing during the wake span -> independent utterances.
        var (arbiter, time, metrics, _) = Create();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        holder.OpenCapture(time, new ArbitrationSettings());
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromSeconds(2));
        waker.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("waker", 600, null, "wake");
        await SettleAsync(time, 500);

        holder.Paused.ShouldBe(0);
        waker.Paused.ShouldBe(0);
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_AlignedLouderHolder_SuppressesTheFreshWakeAsLeak()
    {
        var (arbiter, time, metrics, _) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();

        holder.OpenCapture(time, settings);
        // quiet history, then loud speech exactly at the wake word instant
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(4000));   // the wake word as heard by the holder's mic
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 300, null, "wake"); // far away: much quieter than the holder heard
        await SettleAsync(time, settings.WindowMs);

        waker.Paused.ShouldBe(1);
        holder.Paused.ShouldBe(0);
        holder.Capture.Completed.IsCompleted.ShouldBeFalse("the holder keeps its capture");
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed).Outcome.ShouldBe("leak");
    }

    [Fact]
    public async Task Claim_AlignedMuchLouderFreshWake_HandsOffTheConversation()
    {
        var (arbiter, time, metrics, conversations) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        var conversationId = await conversations.GetOrCreateAsync(
            holder.Session, "agent", "hola", CancellationToken.None);

        holder.OpenCapture(time, settings);
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(600));    // faint leak of the wake word said far from A
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 5000, null, "wake"); // user is right next to B: > 6 dB louder
        await SettleAsync(time, settings.WindowMs);

        holder.Paused.ShouldBe(1);
        (await holder.Capture.Completed).ShouldBe(CaptureOutcome.Abandoned);
        waker.Paused.ShouldBe(0);
        conversations.GetActiveConversationId("waker").ShouldBe(conversationId);
        conversations.GetActiveConversationId("holder").ShouldBeNull();
        metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeHandoff).SatelliteId.ShouldBe("waker");
    }

    // The ordinary field steal: the holder is mid-way through its FIRST utterance, so nothing has
    // dispatched and it owns no conversation to move (bindings are minted at dispatch). The
    // attention still moved, so this is a handoff. Reporting it as stale_steal would blame a
    // staleness that never happened, and — since a holder only owns a binding when an EARLIER
    // utterance of the same conversation already dispatched — that is the path most real steals take.
    [Fact]
    public async Task Claim_StealFromHolderWithNoConversationYet_StillRecordsAHandoff()
    {
        var (arbiter, time, metrics, conversations) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();

        holder.OpenCapture(time, settings);
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(600));    // faint leak of the wake word said far from A
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 5000, null, "wake"); // user is right next to B: > 6 dB louder
        await SettleAsync(time, settings.WindowMs);

        holder.Paused.ShouldBe(1);
        (await holder.Capture.Completed).ShouldBe(CaptureOutcome.Abandoned);
        waker.Paused.ShouldBe(0);
        conversations.GetActiveConversationId("waker").ShouldBeNull();
        var handoff = metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeHandoff);
        handoff.SatelliteId.ShouldBe("waker");
        handoff.Outcome.ShouldBe("holder");
        metrics.Events.OfType<VoiceEvent>().Any(e => e.Metric == VoiceMetric.WakeSuppressed)
            .ShouldBeFalse("the holder's turn moved to the waker, it did not go stale");
    }

    [Fact]
    public async Task Claim_StealDelayedPastWinnersOwnDispatch_KeepsWinnersConversation()
    {
        var (arbiter, time, metrics, conversations) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        var holderConversation = await conversations.GetOrCreateAsync(
            holder.Session, "agent", "hola", CancellationToken.None);

        holder.OpenCapture(time, settings);
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(600));    // faint leak of the wake word said far from A
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 5000, null, "wake"); // loud enough to steal
        await Task.Delay(50); // let the decision task park on its window delay

        // The decision can be delayed past the winner's whole turn (each wedged loser's re-arm
        // costs up to 2 s): by the time the steal runs, the winner has already dispatched and
        // bound its own conversation — with the agent's reply to it still in flight.
        time.Advance(TimeSpan.FromMilliseconds(100));
        var wakerConversation = await conversations.GetOrCreateAsync(
            waker.Session, "agent", "enciende la luz", CancellationToken.None);
        await SettleAsync(time, settings.WindowMs);

        // The stale handoff is skipped so the in-flight reply still routes to the winner,
        // instead of being silently dropped with the winner wedged until the reply timeout.
        conversations.ResolveSatelliteId(wakerConversation).ShouldBe("waker");
        conversations.GetActiveConversationId("waker").ShouldBe(wakerConversation);
        conversations.GetActiveConversationId("holder").ShouldBe(holderConversation);
        // The holder still lost its leaked capture and is silently re-armed.
        holder.Paused.ShouldBe(1);
        (await holder.Capture.Completed).ShouldBe(CaptureOutcome.Abandoned);
        metrics.Events.OfType<VoiceEvent>().Any(e => e.Metric == VoiceMetric.WakeHandoff)
            .ShouldBeFalse("nothing was handed off");
        var stale = metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed);
        stale.SatelliteId.ShouldBe("holder");
        stale.Outcome.ShouldBe("stale_steal");
    }

    [Fact]
    public async Task Claim_DuplicateFromSameSatelliteInWindow_KeepsTheFirstClaim()
    {
        var (arbiter, time, metrics, _) = Create();
        var near = new SatelliteHarness("near", "A");
        var far = new SatelliteHarness("far", "B");
        arbiter.Register("near", near.Handle);
        arbiter.Register("far", far.Handle);
        near.Session.MarkSupportsPause();
        far.Session.MarkSupportsPause();
        near.OpenCapture(time, new ArbitrationSettings());
        far.OpenCapture(time, new ArbitrationSettings());

        arbiter.Claim("near", 900, 0.9, "wake"); // detection carries the loudness
        arbiter.Claim("near", null, null, "wake"); // second claim for the same wake: must be ignored
        arbiter.Claim("far", 200, 0.8, "wake");
        await SettleAsync(time, 500);

        // Had the null-rms duplicate replaced it, near would rank below every reported value and lose.
        far.Paused.ShouldBe(1);
        near.Paused.ShouldBe(0);
        var suppressed = metrics.Events.OfType<VoiceEvent>()
            .Single(e => e.Metric == VoiceMetric.WakeSuppressed);
        suppressed.SatelliteId.ShouldBe("far");
        suppressed.WakeRms.ShouldBe(200);
    }

    [Fact]
    public async Task Claim_HolderCaptureAlreadyEndedNaturally_StealIsAbandoned()
    {
        var (arbiter, time, metrics, conversations) = Create();
        var settings = new ArbitrationSettings();
        var holder = new SatelliteHarness("holder", "A");
        var waker = new SatelliteHarness("waker", "B");
        arbiter.Register("holder", holder.Handle);
        arbiter.Register("waker", waker.Handle);
        holder.Session.MarkSupportsPause();
        waker.Session.MarkSupportsPause();
        var conversationId = await conversations.GetOrCreateAsync(
            holder.Session, "agent", "hola", CancellationToken.None);

        holder.OpenCapture(time, settings);
        holder.Capture!.Feed(SilentChunk());
        time.Advance(TimeSpan.FromMilliseconds(500));
        holder.Capture.Feed(LoudChunk(600));
        time.Advance(TimeSpan.FromMilliseconds(settings.DetectionLatencyMs + 700));
        // The holder's utterance ended on its own, so its transcript dispatch is already in flight:
        // these were two independent turns and there is nothing left to steal.
        holder.Capture.ForceEnd();

        waker.OpenCapture(time, settings);
        arbiter.Claim("waker", 5000, null, "wake"); // loud enough to steal, had there been a capture
        await SettleAsync(time, settings.WindowMs);

        (await holder.Capture.Completed).ShouldBe(CaptureOutcome.Ended);
        holder.Paused.ShouldBe(0);
        waker.Paused.ShouldBe(0);
        conversations.GetActiveConversationId("holder").ShouldBe(conversationId);
        conversations.GetActiveConversationId("waker").ShouldBeNull();
        metrics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claim_LoserPauseWriteThrows_OtherLosersAreStillSuppressed()
    {
        var (arbiter, time, metrics, _) = Create();
        var winner = new SatelliteHarness("winner", "A");
        var dead = new SatelliteHarness("dead", "B") { FailPause = true };
        var alive = new SatelliteHarness("alive", "C");
        foreach (var harness in new[] { winner, dead, alive })
        {
            arbiter.Register(harness.Session.SatelliteId, harness.Handle);
            harness.Session.MarkSupportsPause();
            harness.OpenCapture(time, new ArbitrationSettings());
        }

        arbiter.Claim("dead", 300, null, "wake"); // suppressed first, and its wire write throws
        arbiter.Claim("alive", 200, null, "wake");
        arbiter.Claim("winner", 900, null, "wake");
        await SettleAsync(time, 500, () => alive.Paused == 1 && alive.Capture!.Completed.IsCompleted);

        alive.Paused.ShouldBe(1);
        dead.Paused.ShouldBe(0);
        // Both losers are settled and metered regardless: the abort is local and irreversible, so
        // only the courtesy pause depends on the peer being reachable.
        (await dead.Capture!.Completed).ShouldBe(CaptureOutcome.Abandoned);
        (await alive.Capture!.Completed).ShouldBe(CaptureOutcome.Abandoned);
        winner.Capture!.Completed.IsCompleted.ShouldBeFalse();
        metrics.Events.OfType<VoiceEvent>()
            .Where(e => e.Metric == VoiceMetric.WakeSuppressed)
            .Select(e => e.SatelliteId)
            .ShouldBe(["dead", "alive"], ignoreOrder: true);
    }

    [Fact]
    public async Task Claim_LoserPauseWriteHangs_IsAbandonedSoTheRestOfTheDecisionStillRuns()
    {
        var (arbiter, time, metrics, _) = Create();
        var winner = new SatelliteHarness("winner", "A");
        var wedged = new SatelliteHarness("wedged", "B") { HangPause = true };
        var alive = new SatelliteHarness("alive", "C");
        foreach (var harness in new[] { winner, wedged, alive })
        {
            arbiter.Register(harness.Session.SatelliteId, harness.Handle);
            harness.Session.MarkSupportsPause();
            harness.OpenCapture(time, new ArbitrationSettings());
        }

        arbiter.Claim("wedged", 300, null, "wake"); // suppressed first, and its wire write never returns
        arbiter.Claim("alive", 200, null, "wake");
        arbiter.Claim("winner", 900, null, "wake");
        await SettleAsync(time, 500);
        await wedged.PauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Both losers were settled locally before either write; only the courtesy pause is pending.
        (await wedged.Capture!.Completed).ShouldBe(CaptureOutcome.Abandoned);
        alive.Paused.ShouldBe(0, "the decision is parked on the wedged satellite's write");

        time.Advance(TimeSpan.FromMilliseconds(2001)); // the re-arm deadline
        await WaitUntilAsync(() => alive.Paused == 1,
            "the abandoned write must not stop the next loser being suppressed");

        // The write is abandoned rather than followed, so the remaining loser is still suppressed.
        alive.Paused.ShouldBe(1);
        wedged.Paused.ShouldBe(0);
        winner.Capture!.Completed.IsCompleted.ShouldBeFalse();
        // A timed-out re-arm leaves that satellite streaming with no wake detection until it
        // reconnects, so it is reported as its own event rather than only a swallowed log line.
        metrics.Events.OfType<VoiceEvent>()
            .Where(e => e.Metric == VoiceMetric.WakeSuppressed && e.SatelliteId == "wedged")
            .Select(e => e.Outcome)
            .ShouldBe(["lost_loudness", "rearm_failed"]);
    }

    private static AudioChunk SilentChunk() => PcmChunk(0);
    private static AudioChunk LoudChunk(short amplitude) => PcmChunk(amplitude);

    private static AudioChunk PcmChunk(short amplitude, int samples = 1280)
    {
        var bytes = new byte[samples * 2];
        foreach (var i in Enumerable.Range(0, samples))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), amplitude);
        }
        return new AudioChunk
        {
            Data = bytes,
            Format = new AudioFormat { SampleRateHz = 16000, SampleWidthBytes = 2, Channels = 1 },
            Timestamp = TimeSpan.Zero
        };
    }
}