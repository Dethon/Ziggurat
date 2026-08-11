using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class FollowUpConversationTests
{
    private sealed class Harness
    {
        public readonly List<string> Events = [];
        public readonly FakeTimeProvider Time = new(DateTimeOffset.UtcNow);
        public readonly SatelliteSession Session = new(
            "kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        public readonly List<UtteranceCapture> Dispatched = [];
        public readonly List<CaptureStats> Rejected = [];
        public readonly List<WakeAnnouncement?> WakeAnnouncements = [];
        public bool DispatchResult = true;
        public bool IndicatorWritesThrow;
        public bool CaptureWasOpenAtListeningStarted;
        public int EarlyVerify;
        public bool EarlyRejectResult;
        public TaskCompletionSource<bool>? EarlyRejectGate;

        public Harness() =>
            // The satellite's own control channel, which is where the indicator events land. Driving
            // them through the real writer is what makes their best-effort contract observable.
            Session.ControlWriter = (evt, _) =>
            {
                if (evt.Type == "listening-started")
                {
                    CaptureWasOpenAtListeningStarted = Session.Mic.IsOpen;
                }
                Events.Add(evt.Type);
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
                    Events.Add("open-first");
                    WakeAnnouncements.Add(announcement);
                }),
            Turn = Session.Turn,
            TranscribeAndDispatch = (capture, isFollowUp, _) =>
            {
                Dispatched.Add(capture);
                Events.Add(isFollowUp ? "dispatch-followup" : "dispatch-first");
                return Task.FromResult(DispatchResult);
            },
            EnqueueChime = _ => { Events.Add("chime"); return Task.CompletedTask; },
            EndConversation = _ => { Events.Add("end"); return Task.CompletedTask; },
            OnSilenceTimeout = (stats, _) =>
            {
                Events.Add("timed-out");
                Rejected.Add(stats);
                return Task.CompletedTask;
            },
            OnReplyTimeout = _ => { Events.Add("reply-timeout"); return Task.CompletedTask; },
            EarlyVerifyMs = EarlyVerify,
            EarlyReject = async (_, _) =>
            {
                Events.Add("early-check");
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
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMilliseconds(5000)); // reach the early-verify mark
        await Task.Delay(50);

        h.Events.ShouldContain("early-check");
        h.Events.ShouldContain("end");
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
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMilliseconds(5000)); // early check fires -> allowed -> keep going
        await Task.Delay(50);
        h.EndUtterance();                                // user finishes; capture ends naturally
        await Task.Delay(50);

        h.Events.ShouldContain("early-check");
        h.Events.ShouldContain("dispatch-first");        // allowed voice dispatched normally, not truncated

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
        await Task.Delay(50);
        h.Reply(spoke: true);            // agent reply spoken
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMilliseconds(400)); // tail
        await Task.Delay(50);

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

        sut.OnWake(announcement);
        await Task.Delay(50);

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
        await Task.Delay(50);

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
        await Task.Delay(50);
        h.Reply(spoke: true);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        // Second capture is the follow-up window; feeding only silence => NoSpeech => end.
        h.FeedSilence(6);

        await Task.Delay(50);
        h.Events.ShouldContain("end");
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
        await Task.Delay(50);
        h.Reply(spoke: true);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        h.FeedSilence(6);
        await Task.Delay(50);

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
        await Task.Delay(50);
        h.Reply(spoke: false); // agent produced no audio

        await Task.Delay(50);
        h.Events.ShouldContain("end");
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

        await Task.Delay(50);
        // No reply will ever come; the loop must end instead of blocking on the handshake.
        h.Events.ShouldContain("end");
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
        await Task.Delay(50);
        // The agent never answers, so the turn never settles. The backstop must fire.
        h.Time.Advance(TimeSpan.FromMilliseconds(1000));
        await Task.Delay(50);

        h.Events.ShouldContain("reply-timeout");
        h.Events.ShouldContain("end");

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
        await Task.Delay(50);
        h.Reply(spoke: false);
        await Task.Delay(50);

        // Conversation 2: a brand-new wake must start a fresh first-utterance.
        sut.OnWake(null);
        h.EndUtterance();
        await Task.Delay(50);

        h.Events.Count(e => e == "open-first").ShouldBe(2);
        h.Events.Count(e => e == "dispatch-first").ShouldBe(2);

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
        await Task.Delay(50);
        h.Reply(spoke: true);            // turns=0 < 1 -> opens ONE follow-up window
        await Task.Delay(50);
        // (tail is 0; advance the fake clock in case Task.Delay(0, fakeTime) needs a tick)
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        h.Session.Mic.IsOpen.ShouldBeTrue(); // one follow-up window opened
        h.EndUtterance();                // follow-up utterance
        await Task.Delay(50);
        h.Reply(spoke: true);            // turns=1 >= MaxTurns=1 -> end
        await Task.Delay(50);

        h.Events.ShouldContain("end");
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

        await Task.Delay(50);
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
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMilliseconds(5000)); // early-verify mark: the check is now in flight
        await Task.Delay(50);

        h.Session.Mic.TryAbort().ShouldBeTrue(); // arbiter suppresses this satellite mid-verify
        h.EarlyRejectGate.SetResult(true);  // ...then the verifier rejects the audio so far
        await Task.Delay(50);

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

        await Task.Delay(50);
        h.Dispatched.ShouldBeEmpty();
        h.Events.ShouldNotContain("end"); // the arbiter sends pause-satellite; no transcript here

        // the coordinator must be re-armed: a later wake starts a fresh conversation
        sut.OnWake(null);
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

        await Task.Delay(50);
        h.Events.Count(e => e == "voice-stopped").ShouldBe(1);
        h.Events.IndexOf("voice-stopped").ShouldBeLessThan(h.Events.IndexOf("dispatch-first"));

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

        await Task.Delay(50);
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
        await Task.Delay(50);
        h.Reply(spoke: true);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        // Second capture is the follow-up window; feeding only silence => NoSpeech => end.
        h.FeedSilence(6);

        await Task.Delay(50);
        h.Events.ShouldContain("timed-out");
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
        await Task.Delay(50);
        h.Reply(spoke: true);
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Delay(50);

        h.Events.ShouldContain("listening-started");
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
        await Task.Delay(50);
        h.Reply(spoke: true);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        h.Session.Mic.IsOpen.ShouldBeTrue();

        await StopAsync(sut, run);
    }

    // The events are appended from the conversation's own task, so how long the run takes to reach
    // one is the machine's business. Sleeping a fixed fifty milliseconds and then asserting the
    // whole sequence held until the suite began running enough at once to outlast it, and the
    // failure reads as a wrong order rather than an early look: the list is simply still short.
    //
    // The copy is defensive because Events is a plain List being written to while this reads it —
    // enumerating one mid-Add throws rather than tearing, so a throw here means "it grew, look
    // again" and not a failure.
    private static async Task WaitForEventAsync(Harness h, string terminal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (h.Events.ToArray().Contains(terminal))
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
            }

            await Task.Delay(10);
        }
    }

    private static async Task StopAsync(FollowUpConversation sut, Task run)
    {
        sut.Dispose();
        try
        { await run.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* unwinds on dispose/cancel */ }
    }
}