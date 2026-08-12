using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class InsistentAnnouncementControllerTests
{
    private sealed class CollectingPublisher : IMetricsPublisher
    {
        private readonly List<VoiceEvent> _events = [];
        public IReadOnlyList<VoiceEvent> Events
        {
            get { lock (_events) { return _events.ToList(); } }
        }
        public void Publish(MetricEvent metricEvent)
        {
            if (metricEvent is VoiceEvent v)
            {
                lock (_events)
                { _events.Add(v); }
            }
        }
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunk()
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.CompletedTask;
    }

    private sealed record Harness(
        InsistentAnnouncementController Controller,
        SatelliteSessionRegistry Sessions,
        ActiveAlertRegistry Alerts,
        CollectingPublisher Publisher,
        Mock<ITextToSpeech> Tts,
        FakeTimeProvider Time);

    private static Harness BuildHarness(
        FakeTimeProvider time, bool online, VoiceSettings? settings = null,
        HttpMessageHandler? http = null, params string[] satelliteIds)
    {
        var configs = satelliteIds.ToDictionary(
            id => id, id => new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        var registry = new SatelliteRegistry(configs);
        var sessions = new SatelliteSessionRegistry();
        if (online)
        {
            foreach (var id in satelliteIds)
            {
                sessions.Register(new SatelliteSession(id, configs[id]));
            }
        }

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => OneChunk());

        var alerts = new ActiveAlertRegistry();
        var publisher = new CollectingPublisher();
        var controller = new InsistentAnnouncementController(
            registry, sessions, tts.Object, settings ?? new VoiceSettings(), alerts, publisher, time,
            new StubHttpClientFactory(http ?? new RecordingHandler()),
            NullLogger<InsistentAnnouncementController>.Instance);
        return new Harness(controller, sessions, alerts, publisher, tts, time);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            { throw new TimeoutException("condition not met"); }
            await Task.Delay(20);
        }
    }

    // The loop ends on the token it was started with, exactly as the connection's run does. It may
    // be waiting out a job's nominal audio duration on the injected clock when the cancellation
    // arrives, and that wait is registered asynchronously after the job's last chunk — so push the
    // clock while the cancellation takes effect rather than once.
    private static async Task DrainPumpAsync(Task pump, FakeTimeProvider time, CancellationTokenSource run)
    {
        await run.CancelAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!pump.IsCompleted)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
            { throw new TimeoutException("playback pump did not complete"); }
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }

        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Pushes the fake clock forward until the condition holds, rather than once by the nominal gap.
    // Same hazard as above, on the other side of the round: both the gap delay and the previous
    // round's playback tail are registered AFTER the chunk write that moves the play counter, so a
    // single Advance can land before either timer exists — and then nothing ever moves the clock
    // again, leaving the ring loop and the pump waiting on each other until the test times out.
    private static async Task AdvanceUntilAsync(
        FakeTimeProvider time, Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            { throw new TimeoutException("condition not met while advancing the clock"); }
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }
    }

    // Runs each online session's playback loop so enqueued jobs actually play; the writer counts
    // one invocation per round (the mock TTS yields exactly one chunk).
    private static (Task Pump, Func<int> Count, CancellationTokenSource Run) PumpPlays(
        SatelliteSession session, FakeTimeProvider time)
    {
        var count = 0;
        var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(
            (_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            run.Token, time);
        return (pump, () => Volatile.Read(ref count), run);
    }

    // Records the speaker-volume actions the controller writes to a satellite.
    private static Func<IReadOnlyList<string>> RecordVolumeActions(SatelliteSession session)
    {
        var actions = new List<string>();
        session.ControlWriter = (evt, _) =>
        {
            if (evt.Type == "speaker-volume")
            {
                lock (actions)
                { actions.Add(evt.Data["action"]!.GetValue<string>()); }
            }
            return Task.CompletedTask;
        };
        return () => { lock (actions) { return actions.ToList(); } };
    }

    // A local mute must never swallow a timer or an alarm: the hold unmutes for the ring and the
    // release puts the user's mute back. A one-round alert is the simplest shape of that — one
    // hold, one release — so the speaker is audible for the whole sequence, not part of it.
    [Fact]
    public async Task Start_InsistentAlert_BracketsTheRingWithAlertHoldAndRelease()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var session = h.Sessions.Get("kitchen-01")!;
        var actions = RecordVolumeActions(session);
        var (pump, _, run) = PumpPlays(session, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "eggs",
                Kind = AnnounceKind.Timer,
                Insistent = new() { MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => actions().Contains("alert-release"), TimeSpan.FromSeconds(5));
        actions().ShouldBe(["alert-hold", "alert-release"]);

        await DrainPumpAsync(pump, time, run);
    }

    // The release lives in the loop's finally, so it has to fire on the path that ends an alarm in
    // practice — someone dismissing it — not only on repeats running out.
    [Fact]
    public async Task Start_DismissedMidRing_StillSendsAlertRelease()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var session = h.Sessions.Get("kitchen-01")!;
        var actions = RecordVolumeActions(session);
        var (pump, _, run) = PumpPlays(session, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "wake up",
                Kind = AnnounceKind.Alarm,
                Insistent = new() { MaxRepeats = 12 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => actions().Contains("alert-hold"), TimeSpan.FromSeconds(5));
        h.Alerts.DismissAll();

        await WaitUntilAsync(() => actions().Contains("alert-release"), TimeSpan.FromSeconds(5));

        await DrainPumpAsync(pump, time, run);
    }

    // Overlapping alerts on one satellite are supported by design, so the hold is refcounted: the
    // first alert to finish must not re-mute a speaker the second one is still ringing on.
    [Fact]
    public async Task Start_OverlappingAlerts_ReleasesOnlyAfterTheLastAlertEnds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var session = h.Sessions.Get("kitchen-01")!;
        var actions = RecordVolumeActions(session);
        var (pump, _, run) = PumpPlays(session, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "wake up",
                Kind = AnnounceKind.Alarm,
                Insistent = new() { GapSeconds = 30, MaxRepeats = 12 }
            },
            CancellationToken.None);
        await WaitUntilAsync(() => actions().Contains("alert-hold"), TimeSpan.FromSeconds(5));

        // A short timer on the same satellite, started and finished while the alarm keeps ringing.
        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "eggs",
                Kind = AnnounceKind.Timer,
                Insistent = new() { MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmUnacknowledged),
            TimeSpan.FromSeconds(5));
        await Task.Delay(200); // give a wrongly-fired release a chance to surface

        actions().ShouldNotContain("alert-release");

        h.Alerts.DismissAll();
        await WaitUntilAsync(() => actions().Contains("alert-release"), TimeSpan.FromSeconds(5));

        await DrainPumpAsync(pump, time, run);
    }

    // An alert whose loop dies before it ever sent a hold — synthesis failing is the realistic
    // case — must not release the hold a concurrent alert is relying on.
    [Fact]
    public async Task Start_AlertFailingBeforeItsHold_KeepsAConcurrentAlertsHold()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var session = h.Sessions.Get("kitchen-01")!;
        var actions = RecordVolumeActions(session);
        var (pump, _, run) = PumpPlays(session, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "wake up",
                Kind = AnnounceKind.Alarm,
                Insistent = new() { GapSeconds = 30, MaxRepeats = 12 }
            },
            CancellationToken.None);
        await WaitUntilAsync(() => actions().Contains("alert-hold"), TimeSpan.FromSeconds(5));

        h.Tts.Setup(t => t.SynthesizeAsync("boom", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("tts down"));
        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "boom",
                Kind = AnnounceKind.Timer,
                Insistent = new() { MaxRepeats = 1 }
            },
            CancellationToken.None);

        // The failing alert is gone from the registry; only the ringing alarm is left.
        await WaitUntilAsync(() => h.Alerts.CountFor("kitchen-01") == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(200); // give a wrongly-fired release a chance to surface

        actions().ShouldNotContain("alert-release");

        h.Alerts.DismissAll();
        await WaitUntilAsync(() => actions().Contains("alert-release"), TimeSpan.FromSeconds(5));

        await DrainPumpAsync(pump, time, run);
    }

    // A satellite that reconnects mid-alarm comes back with no hold, and one that was rebooting when
    // the loop started never got one. Re-asserting the hold on every round is what heals both.
    [Fact]
    public async Task Start_MultiRoundAlert_ReassertsAlertHoldEveryRound()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var session = h.Sessions.Get("kitchen-01")!;
        var actions = RecordVolumeActions(session);
        var (pump, plays, run) = PumpPlays(session, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 3 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => plays() >= 2, TimeSpan.FromSeconds(5)); // round 1 (tone + TTS)
        actions().Count(a => a == "alert-hold").ShouldBe(1);

        await AdvanceUntilAsync(time, () => plays() >= 4, TimeSpan.FromSeconds(5)); // round 2 (tone + TTS)
        await WaitUntilAsync(
            () => actions().Count(a => a == "alert-hold") >= 2, TimeSpan.FromSeconds(5));

        h.Alerts.DismissAll();
        await DrainPumpAsync(pump, time, run);
    }

    // Like PumpPlays, but records the alert flag the playback loop reports for each job — the bit
    // the Wyoming host puts on audio-start for the satellite's sink selection.
    private static (Task Pump, Func<IReadOnlyList<bool>> Flags, CancellationTokenSource Run)
        PumpRecordsAlertFlags(SatelliteSession session, FakeTimeProvider time)
    {
        var flags = new List<bool>();
        var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(
            new PlaybackSink(
                (_, _) => Task.CompletedTask,
                OnAudioStart: (_, alert, _) =>
                {
                    lock (flags)
                    { flags.Add(alert); }
                    return Task.CompletedTask;
                }),
            run.Token,
            time);
        return (pump, () => { lock (flags) { return flags.ToList(); } }, run);
    }

    // A timer must ring on the satellite's non-attenuated alert route, not at the calibrated
    // conversational voice level — that per-satellite level is exactly what makes a kitchen
    // countdown inaudible.
    [Fact]
    public async Task Start_Timer_MarksThePlaybackJobAsAnAlert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var (pump, flags, run) = PumpRecordsAlertFlags(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "eggs",
                Kind = AnnounceKind.Timer,
                Insistent = new() { MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => flags().Count >= 1, TimeSpan.FromSeconds(5));
        flags()[0].ShouldBeTrue();

        await DrainPumpAsync(pump, time, run);
    }

    [Fact]
    public async Task Start_UnknownTarget_Throws()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");

        await Should.ThrowAsync<AnnounceTargetNotFoundException>(() =>
            h.Controller.StartAsync(
                new AnnounceRequest { Target = new() { SatelliteId = "ghost" }, Text = "alarm", Insistent = new() },
                CancellationToken.None));
    }

    [Fact]
    public async Task Start_NoAck_RepeatsToCapThenUnacknowledged_SynthesizesOnce()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var (pump, plays, run) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 3 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => plays() >= 2, TimeSpan.FromSeconds(5)); // round 1 (tone + TTS)
        await AdvanceUntilAsync(time, () => plays() >= 4, TimeSpan.FromSeconds(5)); // round 2 (tone + TTS)
        await AdvanceUntilAsync(time, () => plays() >= 6, TimeSpan.FromSeconds(5)); // round 3 (== cap)

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmUnacknowledged),
            TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(60));
        await Eventually.Settle();
        plays().ShouldBe(6); // no 4th round after the cap (2 chunks per round)

        h.Tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once); // synthesized once, replayed across rounds

        await DrainPumpAsync(pump, time, run);
    }

    [Fact]
    public async Task Acknowledge_StopsLoopAndMarksAcknowledged()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var (pump, plays, run) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 10 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => plays() >= 2, TimeSpan.FromSeconds(5)); // tone + TTS

        h.Alerts.Acknowledge("kitchen-01").ShouldNotBeEmpty();

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmAcknowledged),
            TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(120));
        await Eventually.Settle();
        plays().ShouldBe(2); // acknowledged before the second round (tone + TTS)

        await DrainPumpAsync(pump, time, run);
    }

    private static (Task Pump, Func<IReadOnlyList<byte[]>> Chunks, CancellationTokenSource Run) PumpCaptures(
        SatelliteSession session, FakeTimeProvider time)
    {
        var captured = new List<byte[]>();
        var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(
            (chunk, _) => { lock (captured) { captured.Add(chunk.Data.ToArray()); } return Task.CompletedTask; },
            run.Token, time);
        return (pump, () => { lock (captured) { return captured.ToList(); } }, run);
    }

    [Fact]
    public async Task Start_AlarmRound_PlaysToneBeforeSpeech()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var (pump, chunks, run) = PumpCaptures(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => chunks().Count >= 2, TimeSpan.FromSeconds(5)); // tone + TTS chunk
        chunks()[0].ShouldBe(PcmGain.Apply(AlarmTone.Pcm(AnnounceKind.Alarm), 0.5).ToArray());

        // The single round is the last one (MaxRepeats=1), so RunLoopAsync never advances fake time
        // for a gap delay; nothing else unblocks the playback loop's playback-completion wait
        // (chunks written -> waits out totalAudio, real "the satellite finished playing" semantics)
        // for this round's job. Advance past that nominal duration so the loop reaches the next
        // ReadAllAsync iteration and observes the queue's completion.
        time.Advance(TimeSpan.FromSeconds(2));
        await DrainPumpAsync(pump, time, run);
    }

    private static VoiceSettings SettingsWithEscalation() => new()
    {
        Announce = new AnnounceSettings
        {
            Escalation = new EscalationSettings { WebhookUrl = "http://ha:8123/api/webhook/alarm-unacked" }
        }
    };

    [Fact]
    public async Task Unacknowledged_Alarm_PostsEscalationWebhook()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: true, SettingsWithEscalation(), http, "kitchen-01");
        var (pump, plays, run) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "Take out the trash",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => http.Requests.Count == 1, TimeSpan.FromSeconds(5));

        http.Requests[0].Uri!.ToString().ShouldBe("http://ha:8123/api/webhook/alarm-unacked");
        http.Requests[0].Body.ShouldContain("Take out the trash");
        http.Requests[0].Body.ShouldContain("kitchen-01");
        http.Requests[0].Body.ShouldContain("\"rounds\":1");

        // MaxRepeats=1, so RunLoopAsync never advances fake time itself; nothing else unblocks
        // the playback loop's playback-completion wait for round 1's job (see
        // Start_AlarmRound_PlaysToneBeforeSpeech). Advance past its nominal duration first.
        time.Advance(TimeSpan.FromSeconds(2));
        await DrainPumpAsync(pump, time, run);
    }

    [Fact]
    public async Task Start_WakeDuringSlowEscalationWebhook_DoesNotDismissFinishedAlarm()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new BlockingHandler();
        var h = BuildHarness(time, online: true, SettingsWithEscalation(), http, "kitchen-01");
        var (pump, plays, run) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 1 }
            },
            CancellationToken.None);

        await http.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // escalation POST started but not finished

        // The alarm loop already decided "unacknowledged" before the slow POST — its handle must be
        // out of the registry so a wake during the hung webhook can't dismiss a dead alarm.
        h.Alerts.Acknowledge("kitchen-01").ShouldBeEmpty();

        http.Release();
        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmUnacknowledged),
            TimeSpan.FromSeconds(5));

        // MaxRepeats=1, so RunLoopAsync never advances fake time itself; unblock round 1's
        // playback-completion wait before draining (see Start_AlarmRound_PlaysToneBeforeSpeech).
        time.Advance(TimeSpan.FromSeconds(2));
        await DrainPumpAsync(pump, time, run);
    }

    [Fact]
    public async Task Unacknowledged_Timer_DoesNotEscalate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: true, SettingsWithEscalation(), http, "kitchen-01");
        var (pump, plays, run) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "pasta",
                Kind = AnnounceKind.Timer,
                Insistent = new() { GapSeconds = 30, MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmUnacknowledged),
            TimeSpan.FromSeconds(5));
        http.Requests.ShouldBeEmpty();

        // Same fake-time gotcha as above: unblock round 1's playback-completion wait before draining.
        time.Advance(TimeSpan.FromSeconds(2));
        await DrainPumpAsync(pump, time, run);
    }

    [Fact]
    public async Task Acknowledged_Alarm_DoesNotEscalate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: true, SettingsWithEscalation(), http, "kitchen-01");
        var (pump, plays, run) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 10 }
            },
            CancellationToken.None);
        await WaitUntilAsync(() => plays() >= 1, TimeSpan.FromSeconds(5));

        h.Alerts.Acknowledge("kitchen-01").ShouldNotBeEmpty();
        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmAcknowledged),
            TimeSpan.FromSeconds(5));

        http.Requests.ShouldBeEmpty();

        // Same fake-time gotcha (see Acknowledge_StopsLoopAndMarksAcknowledged): unblock round 1's
        // playback-completion wait before draining.
        time.Advance(TimeSpan.FromSeconds(2));
        await DrainPumpAsync(pump, time, run);
    }

    [Fact]
    public async Task Start_AllTargetsOffline_Alarm_PostsEscalationWebhook()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: false, SettingsWithEscalation(), http, "kitchen-01");

        var response = await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" }, Text = "Take out the trash", Insistent = new()
            },
            CancellationToken.None);

        response.Satellites.ShouldHaveSingleItem();
        response.Satellites[0].Status.ShouldBe("offline");
        await WaitUntilAsync(() => http.Requests.Count == 1, TimeSpan.FromSeconds(5));
        http.Requests[0].Uri!.ToString().ShouldBe("http://ha:8123/api/webhook/alarm-unacked");
        http.Requests[0].Body.ShouldContain("Take out the trash");
        http.Requests[0].Body.ShouldContain("kitchen-01");
        http.Requests[0].Body.ShouldContain("\"rounds\":0");
    }

    [Fact]
    public async Task Start_AllTargetsOffline_Timer_DoesNotEscalate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: false, SettingsWithEscalation(), http, "kitchen-01");

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "pasta",
                Kind = AnnounceKind.Timer,
                Insistent = new()
            },
            CancellationToken.None);

        h.Publisher.Events.ShouldContain(e => e.Metric == VoiceMetric.AlarmOffline);
        await Task.Delay(100); // give a wrongly-fired escalation a chance to surface
        http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_MixedOfflineTargets_PublishesAlarmOfflineForOfflineSatelliteOnly()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: false, satelliteIds: ["kitchen-01", "office-01"]);
        h.Sessions.Register(new SatelliteSession(
            "kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" }));

        var response = await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteIds = ["kitchen-01", "office-01"] },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 10 }
            },
            CancellationToken.None);

        response.Satellites.Single(s => s.Id == "kitchen-01").Status.ShouldBe("started");
        response.Satellites.Single(s => s.Id == "office-01").Status.ShouldBe("offline");
        h.Publisher.Events.ShouldContain(e => e.Metric == VoiceMetric.AlarmOffline && e.SatelliteId == "office-01");
        h.Publisher.Events.ShouldNotContain(e => e.Metric == VoiceMetric.AlarmOffline && e.SatelliteId == "kitchen-01");

        h.Alerts.Acknowledge("kitchen-01"); // stop the ring loop
    }
}