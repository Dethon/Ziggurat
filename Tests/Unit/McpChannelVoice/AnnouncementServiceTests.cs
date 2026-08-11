using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AnnouncementServiceTests
{
    private static async IAsyncEnumerable<AudioChunk> FakeAudio()
    {
        yield return new AudioChunk
        {
            Data = new byte[16],
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    private (AnnouncementService Sut, SatelliteSessionRegistry SessionReg) BuildSut(params (string Id, string Room)[] sats)
    {
        var (sut, sessions, _) = BuildRecordingSut(sats);
        return (sut, sessions);
    }

    private (AnnouncementService Sut, SatelliteSessionRegistry Sessions, List<VoiceEvent> Published)
        BuildRecordingSut(
            (string Id, string Room)[] sats,
            Func<IAsyncEnumerable<AudioChunk>>? audio = null,
            Func<PlaybackQueue>? queue = null)
    {
        var sessions = new SatelliteSessionRegistry();
        foreach (var (id, room) in sats)
        {
            sessions.Register(new SatelliteSession(
                id, new SatelliteConfig { Identity = "household", Room = room }, queue?.Invoke()));
        }

        var registry = new SatelliteRegistry(sats.ToDictionary(
            s => s.Id,
            s => new SatelliteConfig { Identity = "household", Room = s.Room }));

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => audio?.Invoke() ?? FakeAudio());

        var published = new List<VoiceEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(evt =>
            {
                if (evt is VoiceEvent voiceEvent)
                {
                    lock (published)
                    { published.Add(voiceEvent); }
                }
            });

        var sut = new AnnouncementService(registry, sessions, tts.Object, new VoiceSettings(),
            publisher.Object, NullLogger<AnnouncementService>.Instance);
        return (sut, sessions, published);
    }

    [Fact]
    public async Task Announce_BySatelliteId_TargetsOne()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" },
            CancellationToken.None);

        response.Satellites.Count.ShouldBe(1);
        response.Satellites[0].Id.ShouldBe("kitchen-01");
        response.Satellites[0].Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Announce_ByRoom_TargetsAllInRoom()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("kitchen-02", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { Room = "Kitchen" }, Text = "hi" },
            CancellationToken.None);

        response.Satellites.Select(s => s.Id).ShouldBe(["kitchen-01", "kitchen-02"], ignoreOrder: true);
    }

    [Fact]
    public async Task Announce_UnknownTarget_Throws404Equivalent()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"));

        await Should.ThrowAsync<AnnounceTargetNotFoundException>(
            () => sut.AnnounceAsync(
                new AnnounceRequest { Target = new() { SatelliteId = "ghost" }, Text = "hi" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Announce_HighPriority_PreemptsCurrentReply()
    {
        var (sut, sessions) = BuildSut(("kitchen-01", "Kitchen"));
        var session = sessions.Get("kitchen-01")!;
        var started = new TaskCompletionSource();
        var pump = session.Playback.RunAsync((c, ct) => Task.Delay(50, ct), CancellationToken.None);
        var ongoing = session.Playback.Enqueue(
            new PlaybackJob("ongoing", PlaybackKind.Announce, AnnouncePriority.Normal, NeverEnding(),
                OnFirstAudio: _ => { started.TrySetResult(); return Task.CompletedTask; }));

        // Wait until the playback loop has actually picked up the ongoing job
        // before issuing the high-priority preemption (otherwise PreemptCurrent
        // has no current playback to cancel).
        (await Task.WhenAny(started.Task, Task.Delay(2_000))).ShouldBe(started.Task);

        await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "alert", Priority = AnnouncePriority.High },
            CancellationToken.None);

        (await ongoing.Completed.WaitAsync(TimeSpan.FromSeconds(5)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
    }

    // A speaker that went away and a speaker that is busy used to look the same to the caller: both
    // came back "dropped". The refusal reason tells them apart.
    [Fact]
    public async Task Announce_SatelliteWentAwayBeforeTheEnqueue_ReportsOffline()
    {
        var (sut, sessions, published) = BuildRecordingSut([("kitchen-01", "Kitchen")]);
        sessions.Get("kitchen-01")!.Playback.CompleteAndDiscardQueued();   // the link dropped

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" },
            CancellationToken.None);

        response.Satellites[0].Status.ShouldBe("offline");
        var error = published.Single(e => e.Metric == VoiceMetric.AnnounceError);
        error.Outcome.ShouldBe("offline");
        error.Room.ShouldBe("Kitchen");
        error.Identity.ShouldBe("household");
    }

    [Fact]
    public async Task Announce_QueueAlreadyAtItsAllowance_ReportsDropped()
    {
        var (sut, sessions, published) = BuildRecordingSut(
            [("kitchen-01", "Kitchen")], queue: () => new PlaybackQueue(announceMaxDepth: 1));
        // Nothing is draining, so the one job already queued fills the announce allowance.
        sessions.Get("kitchen-01")!.Playback.Enqueue(new PlaybackJob(
            "ongoing", PlaybackKind.Announce, AnnouncePriority.Normal, FakeAudio()));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" },
            CancellationToken.None);

        response.Satellites[0].Status.ShouldBe("dropped");
        published.Single(e => e.Metric == VoiceMetric.AnnounceError).Outcome.ShouldBe("dropped");
    }

    [Fact]
    public async Task Announce_SynthesisFails_PublishesNoPlayedMetric()
    {
        // Played was published the moment the queue reached the job, before any audio existed, so an
        // announcement whose synthesis then failed was counted as played and nothing else was ever
        // recorded for it.
        var (sut, sessions, published) = BuildRecordingSut(
            [("kitchen-01", "Kitchen")], audio: PlaybackFakes.ThrowingAudio);
        var session = sessions.Get("kitchen-01")!;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(
            new PlaybackSink(
                (_, _) => Task.CompletedTask,
                OnError: (_, _) => { failed.TrySetResult(); return Task.CompletedTask; }),
            run.Token);

        await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" },
            CancellationToken.None);
        // The synthesis failing is what this is about, so wait for the queue to report it rather
        // than for the loop to end.
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        published.ShouldContain(e => e.Metric == VoiceMetric.AnnounceQueued);
        published.ShouldNotContain(e => e.Metric == VoiceMetric.AnnouncePlayed);
    }

    [Fact]
    public async Task Announce_AudioReachesTheSatellite_PublishesPlayed()
    {
        var (sut, sessions, published) = BuildRecordingSut([("kitchen-01", "Kitchen")]);
        var session = sessions.Get("kitchen-01")!;
        var wrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(
            (_, _) => { wrote.TrySetResult(); return Task.CompletedTask; }, run.Token);

        await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" },
            CancellationToken.None);
        await wrote.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => published.Any(e => e.Metric == VoiceMetric.AnnouncePlayed));
        await run.StopAsync(pump);

        var played = published.Single(e => e.Metric == VoiceMetric.AnnouncePlayed);
        played.Room.ShouldBe("Kitchen");
        played.Identity.ShouldBe("household");
    }

    private static async IAsyncEnumerable<AudioChunk> NeverEnding()
    {
        while (true)
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            await Task.Delay(10);
        }
    }

    // A plain announcement (a download finishing, a reminder read out) is NOT an alert: it must
    // keep the calibrated per-satellite voice level rather than ringing at full scale.
    [Fact]
    public async Task Announce_PlainAnnouncement_IsNotMarkedAsAnAlert()
    {
        var (sut, sessions) = BuildSut(("kitchen-01", "Kitchen"));
        var session = sessions.Get("kitchen-01")!;
        var flags = new List<bool>();
        using var run = new CancellationTokenSource();
        var pump = session.Playback.RunAsync(
            new PlaybackSink(
                (_, _) => Task.CompletedTask,
                OnAudioStart: (_, alert, _) =>
                {
                    lock (flags)
                    { flags.Add(alert); }
                    return Task.CompletedTask;
                }),
            run.Token);

        await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "download done" },
            CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (flags.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }

        flags.ShouldHaveSingleItem();
        flags[0].ShouldBeFalse();

        await run.StopAsync(pump);
    }

    // A metric is published from the queue's own loop, so a test that asserts on one waits for it
    // rather than for the loop to end.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}