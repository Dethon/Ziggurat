using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class FollowUpConversationTests
{
    private sealed class Harness
    {
        // Everything the conversation records is written from its own task and read from the test
        // thread. A plain List torn between the two throws out of the read rather than returning a
        // stale answer, so the lists are private and every reader gets a snapshot.
        private readonly Lock _gate = new();
        private readonly List<string> _events = [];
        private readonly List<UtteranceCapture> _dispatched = [];
        private readonly List<CaptureStats> _rejected = [];
        private readonly List<WakeAnnouncement?> _wakeAnnouncements = [];

        public readonly ArmedClock Time = new();
        public readonly SatelliteSession Session = new(
            "kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        public bool DispatchResult = true;
        public bool IndicatorWritesThrow;
        public bool CaptureWasOpenAtListeningStarted;
        public int EarlyVerify;
        public bool EarlyRejectResult;
        public TaskCompletionSource<bool>? EarlyRejectGate;

        public List<string> Events => Snapshot(_events);
        public List<UtteranceCapture> Dispatched => Snapshot(_dispatched);
        public List<CaptureStats> Rejected => Snapshot(_rejected);
        public List<WakeAnnouncement?> WakeAnnouncements => Snapshot(_wakeAnnouncements);

        private List<T> Snapshot<T>(List<T> of)
        {
            lock (_gate)
            {
                return [.. of];
            }
        }

        private void Record<T>(List<T> to, T item)
        {
            lock (_gate)
            {
                to.Add(item);
            }
        }

        public Harness() =>
            // The satellite's own control channel, which is where the indicator events land. Driving
            // them through the real writer is what makes their best-effort contract observable.
            Session.ControlWriter = (evt, _) =>
            {
                if (evt.Type == "listening-started")
                {
                    CaptureWasOpenAtListeningStarted = Session.Mic.IsOpen;
                }
                Record(_events, evt.Type);
                return IndicatorWritesThrow
                    ? throw new IOException("satellite write failed")
                    : Task.CompletedTask;
            };

        public FollowUpConversation Build(FollowUpSettings followUp) => new(followUp, Time)
        {
            Capture = new CaptureSession(
                Session,
                new SilenceGateFactory(
                    new VoiceSettings { FollowUp = followUp },
                    new WyomingClientSettings
                    {
                        SilenceRmsThreshold = 500,
                        TrailingSilenceMs = 200,
                        MaxUtteranceMs = 5000,
                        MinSpeechMs = 100
                    },
                    Time),
                Time,
                TimeSpan.FromSeconds(5),
                // Only the wake turn has a hook, and only the wake turn carries an announcement —
                // that is the whole point of the split. A follow-up window is observed through the
                // capture it opens instead.
                onWakeTurn: announcement =>
                {
                    Record(_events, "open-first");
                    Record(_wakeAnnouncements, announcement);
                }),
            Turn = Session.Turn,
            TranscribeAndDispatch = (capture, isFollowUp, _) =>
            {
                Record(_dispatched, capture);
                Record(_events, isFollowUp ? "dispatch-followup" : "dispatch-first");
                return Task.FromResult(DispatchResult);
            },
            EnqueueChime = _ => { Record(_events, "chime"); return Task.CompletedTask; },
            EndConversation = _ => { Record(_events, "end"); return Task.CompletedTask; },
            OnSilenceTimeout = (stats, _) =>
            {
                Record(_events, "timed-out");
                Record(_rejected, stats);
                return Task.CompletedTask;
            },
            OnReplyTimeout = _ => { Record(_events, "reply-timeout"); return Task.CompletedTask; },
            EarlyVerifyMs = EarlyVerify,
            EarlyReject = async (_, _) =>
            {
                Record(_events, "early-check");
                return EarlyRejectGate is { } gate ? await gate.Task : EarlyRejectResult;
            }
        };

        // The user finishes speaking. The satellite's read loop is what ends a capture in
        // production, so the test ends it the same way.
        public void EndUtterance() => Session.Mic.ForceEnd();

        public void FeedSilence(int chunks)
        {
            var silent = new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard };
            foreach (var _ in Enumerable.Range(0, chunks))
            { Session.Mic.Feed(silent); }
        }

        // The agent's answer, over the real reply path: one segment queued and played, then the
        // stream ends. A silent turn queues nothing and just ends the stream.
        public void Reply(bool spoke)
        {
            if (spoke)
            {
                Session.Turn.BeginSegment().Complete();
            }
            Session.Turn.EndStream();
        }
    }

    [Fact]
    public async Task Disabled_DispatchesThenEndsImmediately_NoReplyWait()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance(); // utterance ended (speech)

        await WaitForEventAsync(h, "end");
        h.Events.ShouldBe(["open-first", "voice-stopped", "dispatch-first", "end"]);
        h.Events.ShouldNotContain("timed-out");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task EarlyReject_UnknownVoiceMidCapture_ClosesWithoutDispatch()
    {
        var h = new Harness { EarlyVerify = 5000, EarlyRejectResult = true };
        var sut = h.Build(new FollowUpSettings { Enabled = true, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);                                    // capture opens and keeps running (no ForceEnd)
        await h.Time.AdvancePastAsync(TimeSpan.FromMilliseconds(5000)); // reach the early-verify mark

        await WaitForEventAsync(h, "end");
        h.Events.ShouldContain("early-check");
        h.Dispatched.ShouldBeEmpty();                    // unknown speaker -> never reaches the agent
        h.Events.ShouldNotContain("dispatch-first");
        h.Events.ShouldNotContain("voice-stopped");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task EarlyReject_AllowedVoiceMidCapture_ContinuesToNaturalEnd()
    {
        var h = new Harness { EarlyVerify = 5000, EarlyRejectResult = false };
        var sut = h.Build(new FollowUpSettings { Enabled = false, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        await h.Time.AdvancePastAsync(TimeSpan.FromMilliseconds(5000)); // early check fires -> allowed
        await WaitForEventAsync(h, "early-check");
        h.EndUtterance();                                // user finishes; capture ends naturally

        // Allowed voice dispatched normally, not truncated.
        await WaitForEventAsync(h, "dispatch-first");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SpeechReplyThenFollowUp_OpensSecondWindowWithoutWake()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = true, PlaybackTailMs = 400, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();                // first utterance ends
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: true);            // agent reply spoken
        await h.Time.AdvancePastAsync(TimeSpan.FromMilliseconds(400)); // tail
        await WaitForFollowUpWindowAsync(h);

        // The chime is queued before the tail begins, so arming that tail is already past it.
        h.Events.ShouldContain("chime");

        // The follow-up window opened a second capture without a new wake: the mic is live again
        // and the wake-turn hook — the only thing a wake can fire — ran exactly once.
        h.Session.Mic.IsOpen.ShouldBeTrue();
        h.Events.Count(e => e == "open-first").ShouldBe(1);

        await StopAsync(sut, run);
    }

    // The announcement travels from the frame that carried it to the turn that opened on it, as an
    // argument. Nothing stores it in between, so there is nothing for a later turn to pick up.
    [Fact]
    public async Task OnWake_CarriesTheAnnouncementThroughToTheWakeTurn()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);
        var announcement = new WakeAnnouncement(1234.5, 0.87, "wake", RoomRms: 68.25);

        // OpenWakeTurn runs on the caller, so the announcement has already been spent by the time
        // OnWake returns and there is nothing to wait for.
        sut.OnWake(announcement);

        h.WakeAnnouncements.ShouldHaveSingleItem().ShouldBe(announcement);

        await StopAsync(sut, run);
    }

    // A wake arriving while a turn is already open is dropped, and its announcement with it. That
    // early return is what replaced the explicit "drop whatever nobody consumed" step.
    [Fact]
    public async Task OnWake_TurnAlreadyOpen_DiscardsTheSecondAnnouncement()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        sut.OnWake(new WakeAnnouncement(1234.5, 0.87, "wake"));

        // Both calls returned, so the second one's early exit has already happened or not at all.
        h.WakeAnnouncements.ShouldHaveSingleItem().ShouldBeNull();

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_FollowUpSilence_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: true);            // tail is 0, so the window opens without the clock moving
        await WaitForFollowUpWindowAsync(h);

        // Second capture is the follow-up window; feeding only silence => NoSpeech => end.
        h.FeedSilence(6);

        await WaitForEventAsync(h, "end");
        h.Events.ShouldContain("timed-out");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_FollowUpSilence_ReportsRejectedCaptureStats()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: true);            // tail is 0, so the window opens without the clock moving
        await WaitForFollowUpWindowAsync(h);

        h.FeedSilence(6);
        await WaitForEventAsync(h, "end");

        // The rejected capture's gate stats reach the timeout callback, so the host can
        // publish them — rejection decisions must be tunable from field data, not guesswork.
        h.Rejected.ShouldHaveSingleItem().EndReason.ShouldBe("no_speech");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SilentTurn_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: false); // agent produced no audio

        await WaitForEventAsync(h, "end");
        h.Events.ShouldNotContain("chime");
        h.Events.ShouldNotContain("timed-out");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_DispatchDidNotReachAgent_EndsWithoutWaitingForReply()
    {
        var h = new Harness { DispatchResult = false }; // empty/low-confidence transcript, no agent message
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance(); // utterance ended (speech), but transcript drops -> nothing dispatched

        // No reply will ever come; the loop must end instead of blocking on the handshake.
        await WaitForEventAsync(h, "end");
        h.Events.ShouldNotContain("chime");
        h.Events.ShouldNotContain("reply-timeout");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_ReplyNeverResolves_TimesOutAndEndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, ReplyTimeoutMs = 1000 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();
        // The agent never answers, so the turn never settles. The backstop must fire — but only
        // once the loop has armed it.
        await h.Time.AdvancePastAsync(TimeSpan.FromMilliseconds(1000));

        await WaitForEventAsync(h, "end");
        h.Events.ShouldContain("reply-timeout");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SecondWakeAfterConversationEnds_StartsFreshConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        // Conversation 1: wake, speak, agent stays silent -> ends.
        sut.OnWake(null);
        h.EndUtterance();
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: false);
        await WaitForEventAsync(h, "end");

        // Conversation 2: a brand-new wake must start a fresh first-utterance.
        await WakeAgainAsync(sut, h, 2);
        h.EndUtterance();
        await WaitForCountAsync(h, "dispatch-first", 2);

        // Exactly two of each: the wait above only holds the test until the second one lands, and
        // two fresh conversations must produce no more than that between them.
        var events = h.Events;
        events.Count(e => e == "open-first").ShouldBe(2);
        events.Count(e => e == "dispatch-first").ShouldBe(2);

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_MaxTurnsReached_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, MaxTurns = 1 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();                // first utterance
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: true);            // turns=0 < 1 -> opens ONE follow-up window
        await WaitForFollowUpWindowAsync(h); // tail is 0, so no clock move is needed

        h.EndUtterance();                // follow-up utterance
        await WaitForEventAsync(h, "dispatch-followup");
        h.Reply(spoke: true);            // turns=1 >= MaxTurns=1 -> end
        await WaitForEventAsync(h, "end");
        h.Events.ShouldNotContain("timed-out");
        // Capped at one: the first utterance plus a single follow-up reached the dispatcher, and
        // the mic is closed rather than open on a second window.
        h.Dispatched.Count.ShouldBe(2);
        h.Session.Mic.IsOpen.ShouldBeFalse();
        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Dispatch_ReceivesTheCaptureItOpened()
    {
        // The dispatcher needs the capture itself (audio + gate stats), not just the audio stream.
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();

        await WaitForEventAsync(h, "dispatch-first");
        // Gate stats and all, not just an audio stream: "forced" is the end reason the satellite's
        // own audio-stop produces, so this is the very capture the loop opened and ended.
        h.Dispatched.ShouldHaveSingleItem().Stats.EndReason.ShouldBe("forced");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task EarlyReject_AbortedByArbiterDuringVerify_ExitsWithoutEnd()
    {
        var h = new Harness
        {
            EarlyVerify = 5000,
            EarlyRejectGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var sut = h.Build(new FollowUpSettings { Enabled = true, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        await h.Time.AdvancePastAsync(TimeSpan.FromMilliseconds(5000)); // early-verify mark
        await WaitForEventAsync(h, "early-check");      // the check is now in flight

        h.Session.Mic.TryAbort().ShouldBeTrue(); // arbiter suppresses this satellite mid-verify
        h.EarlyRejectGate.SetResult(true);  // ...then the verifier rejects the audio so far

        // What is claimed from here is an absence, which no state announces: give the wrong
        // behaviour room to appear instead of reading the instant the gate is released.
        await Eventually.Settle();
        h.Events.ShouldContain("early-check");
        // The arbiter's pause-satellite already re-armed the satellite; a transcript here
        // would audibly done-cue a lost turn.
        h.Events.ShouldNotContain("end");
        h.Dispatched.ShouldBeEmpty();

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Abandoned_ArbitrationLoss_ExitsWithoutDispatchOrEnd()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.Session.Mic.TryAbort().ShouldBeTrue(); // arbiter suppressed this satellite

        await Eventually.Settle();
        h.Dispatched.ShouldBeEmpty();
        h.Events.ShouldNotContain("end"); // the arbiter sends pause-satellite; no transcript here

        // the coordinator must be re-armed: a later wake starts a fresh conversation
        await WakeAgainAsync(sut, h, 2);

        // Exactly two, not merely two by the time the wake took: retrying a wake that OnWake
        // discards must not leave a second turn opened behind the one this asked for.
        h.Events.Count(e => e == "open-first").ShouldBe(2);
        h.Session.Mic.IsOpen.ShouldBeTrue();

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task SpeechStopped_AcceptedCapture_FiresOnceBeforeDispatch()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance(); // utterance ended (speech)

        await WaitForEventAsync(h, "dispatch-first");
        // One snapshot, so the two positions are read off the same list rather than two.
        var events = h.Events;
        events.Count(e => e == "voice-stopped").ShouldBe(1);
        events.IndexOf("voice-stopped").ShouldBeLessThan(events.IndexOf("dispatch-first"));

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task SpeechStopped_AbandonedByArbitration_NeverFires()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.Session.Mic.TryAbort().ShouldBeTrue(); // arbiter suppressed this satellite

        await Eventually.Settle();
        h.Events.ShouldNotContain("voice-stopped");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task SpeechStopped_NoSpeechFollowUp_NeverFiresForThatCapture()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance(); // first utterance accepted -> one voice-stopped
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: true);            // tail is 0, so the window opens without the clock moving
        await WaitForFollowUpWindowAsync(h);

        // Second capture is the follow-up window; feeding only silence => NoSpeech => end.
        h.FeedSilence(6);

        await WaitForEventAsync(h, "timed-out");
        // Only the accepted first capture fired it; the silent follow-up must not add another.
        h.Events.Count(e => e == "voice-stopped").ShouldBe(1);

        await StopAsync(sut, run);
    }

    // The satellite has no other way to know the mic reopened: its own capture never closed, so
    // after a reply drains its ring stays on the "agent is working" look until something says
    // otherwise. Announce the window before opening it, so the light and the live mic agree.
    [Fact]
    public async Task Enabled_FollowUpWindow_AnnouncesListeningBeforeOpeningTheCapture()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = true, PlaybackTailMs = 400, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: true);
        await h.Time.AdvancePastAsync(TimeSpan.FromMilliseconds(400));

        await WaitForEventAsync(h, "listening-started");
        h.CaptureWasOpenAtListeningStarted.ShouldBeFalse();
        // The first capture comes from the satellite's own wake, which lights its ring locally.
        h.Events.Count(e => e == "listening-started").ShouldBe(1);

        await StopAsync(sut, run);
    }

    // Same contract as SpeechStopped: this drives an indicator only, so a satellite write that
    // fails must never cost the user the follow-up window it was announcing.
    [Fact]
    public async Task Enabled_ListeningStartedThrows_StillOpensTheFollowUpWindow()
    {
        var h = new Harness { IndicatorWritesThrow = true };
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake(null);
        h.EndUtterance();
        await WaitForEventAsync(h, "dispatch-first");
        h.Reply(spoke: true);            // tail is 0, so the window opens without the clock moving

        await WaitForFollowUpWindowAsync(h);
        h.Session.Mic.IsOpen.ShouldBeTrue();

        await StopAsync(sut, run);
    }

    // The events are appended from the conversation's own task, so how long the run takes to reach
    // one is the machine's business. Sleeping a fixed fifty milliseconds and then asserting the
    // whole sequence held until the suite began running enough at once to outlast it, and the
    // failure reads as a wrong order rather than an early look: the list is simply still short.
    private static Task WaitForEventAsync(Harness h, string terminal) =>
        Eventually.Until(() => h.Events.Contains(terminal), $"the conversation to reach '{terminal}'");

    private static Task WaitForCountAsync(Harness h, string repeated, int times) =>
        Eventually.Until(
            () => h.Events.Count(e => e == repeated) >= times, $"'{repeated}' to happen {times} times");

    // A follow-up window is not announced by an event of its own — the capture it opens is the
    // window — so the live microphone is what says the loop got there.
    private static Task WaitForFollowUpWindowAsync(Harness h) =>
        Eventually.Until(() => h.Session.Mic.IsOpen, "the follow-up window to open a capture");

    // A conversation clears its active flag in a finally, after the transcript that ended it has
    // already been recorded, so a wake sent the instant "end" appears can still land inside the old
    // conversation and be dropped by design. Waking until one takes is the honest way to ask for the
    // next conversation; OnWake on a live turn returns without doing anything, so repeating is free.
    private static Task WakeAgainAsync(FollowUpConversation sut, Harness h, int wakeNumber) =>
        Eventually.Until(
            () =>
            {
                sut.OnWake(null);
                return h.Events.Count(e => e == "open-first") >= wakeNumber;
            },
            $"wake {wakeNumber} to open a fresh turn");

    private static async Task StopAsync(FollowUpConversation sut, Task run)
    {
        sut.Dispose();
        try
        { await run.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* unwinds on dispose/cancel */ }
    }
}