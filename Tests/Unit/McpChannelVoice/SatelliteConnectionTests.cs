using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Mcp.Hosting;

namespace Tests.Unit.McpChannelVoice;

// One run of the link to a satellite, driven by pushing Wyoming events into it and reading back what
// the satellite would have received. The host assembles the connection, so everything under the
// wire is the real thing — the satellite session, both registries, the arbiter, the coordinator, the
// capture session, the silence gate and its factory, the transcript dispatcher and the metric
// publishes. Only the socket is gone.
public class SatelliteConnectionTests
{
    private sealed class Harness
    {
        public WyomingClientSettings Wyoming = new()
        {
            ReconnectDelaySeconds = 1,
            SilenceRmsThreshold = 500,
            TrailingSilenceMs = 200,
            MaxUtteranceMs = 3000,
            MinSpeechMs = 100
        };
        public VoiceSettings Voice = new()
        {
            AgentId = "mycroft",
            FollowUp = new FollowUpSettings { Enabled = false }
        };
        public SatelliteConfig Config = new()
        {
            Identity = "household",
            Room = "Kitchen",
            WakeWord = "hey_jarvis"
        };
        public string TranscriptText = "hola";
        public ISpeakerVerifier? Verifier;
        // A conversation's FIRST turn — the normal case for a one-shot command, given the 5-minute
        // mapping expiry — makes GetOrCreateAsync a full create_conversation MCP round trip to the
        // agent process. Exaggerated by a test that needs the gap unambiguous.
        public TimeSpan CreateConversationDelay = TimeSpan.Zero;
        // Runs on the connection's own writer, so a test can park a write the way a dead socket
        // parks one.
        public Func<WyomingEvent, CancellationToken, Task>? WriteHook;

        public readonly ActiveAlertRegistry Alerts = new();
        public readonly SatelliteSessionRegistry Sessions = new();
        public readonly ChannelInboxProbe Emitter = new("voice", DeliveryPolicy.Broadcast);
        public readonly List<VoiceEvent> Published = [];
        public readonly Channel<WyomingEvent> Inbound = Channel.CreateUnbounded<WyomingEvent>();
        public int ChunksTranscribed;
        public long SttFinishedAt;
        public TranscriptionOptions? SttOptions;
        public Mock<ISpeechToText> Stt = null!;
        public WakeArbiter Arbiter = null!;

        private readonly List<WyomingEvent> _written = [];

        public SatelliteConnection Build(string id = "kitchen-01")
        {
            var publisher = new Mock<IMetricsPublisher>();
            publisher.Setup(p => p.Publish(It.IsAny<MetricEvent>()))
                .Callback<MetricEvent>(evt =>
                {
                    if (evt is VoiceEvent voiceEvent)
                    {
                        lock (Published)
                        { Published.Add(voiceEvent); }
                    }
                });

            var factory = new Mock<IConversationFactory>();
            factory.Setup(f => f.CreateAsync(
                    It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    if (CreateConversationDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(CreateConversationDelay);
                    }
                    var identity = ConversationIdGenerator.CreateFor("topic-x");
                    var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                        $"{Config.Identity} @ {Config.Room}", DateTimeOffset.UtcNow, null);
                    return new ConversationCreation(identity, topic);
                });
            var manager = new VoiceConversationManager(
                factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

            Stt = new Mock<ISpeechToText>();
            Stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                             It.IsAny<TranscriptionOptions>(),
                                             It.IsAny<CancellationToken>()))
                .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                    async (audio, options, token) =>
                    {
                        SttOptions = options;
                        await foreach (var chunk in audio.WithCancellation(token))
                        {
                            Interlocked.Increment(ref ChunksTranscribed);
                        }
                        SttFinishedAt = TimeProvider.System.GetTimestamp();
                        return new TranscriptionResult
                        {
                            Text = TranscriptText,
                            Language = "es",
                            Confidence = TranscriptText.Length == 0 ? 0.0 : 0.9
                        };
                    });

            var dispatcher = new TranscriptDispatcher(
                Emitter.Emitter, publisher.Object, manager,
                new LocalCommandDispatcher(
                    new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
                new ReplyTextAccumulator(),
                -1.0, 0.6, -1.4, 2000, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);

            // Arbitration no-ops below two registered handles, so a default instance keeps this
            // construction real without changing what it exercises. Multi-satellite arbitration has
            // its own end-to-end coverage.
            Arbiter = new WakeArbiter(new ArbitrationSettings(), manager, publisher.Object,
                TimeProvider.System, NullLogger<WakeArbiter>.Instance);

            var host = new WyomingSatelliteHost(
                Wyoming, Voice,
                new SatelliteRegistry(new Dictionary<string, SatelliteConfig> { [id] = Config }),
                Sessions, manager, Stt.Object, dispatcher, Alerts, publisher.Object,
                TimeProvider.System, Arbiter,
                new SilenceGateFactory(Voice, Wyoming, TimeProvider.System),
                NullLogger<WyomingSatelliteHost>.Instance,
                Verifier);

            return host.CreateConnection(id, Config, WriteAsync);
        }

        private async Task WriteAsync(WyomingEvent evt, CancellationToken ct)
        {
            lock (_written)
            { _written.Add(evt); }
            if (WriteHook is { } hook)
            {
                await hook(evt, ct);
            }
        }

        public IAsyncEnumerable<WyomingEvent> Events => Inbound.Reader.ReadAllAsync();

        public void Send(WyomingEvent evt) => Inbound.Writer.TryWrite(evt);

        public void SendWake(JsonObject? data = null) =>
            Send(WyomingEvent.Header("run-pipeline", data ?? []));

        public void SendAudio(short level, int chunks)
        {
            var format = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };
            foreach (var _ in Enumerable.Range(0, chunks))
            {
                Send(WyomingEvent.WithPayload("audio-chunk", format.DeepClone().AsObject(), Pcm(level)));
            }
        }

        public void DropLink() => Inbound.Writer.TryComplete();

        public IReadOnlyList<WyomingEvent> WrittenOfType(string type)
        {
            lock (_written)
            { return _written.Where(e => e.Type == type).ToList(); }
        }

        public VoiceEvent[] PublishedOf(VoiceMetric metric)
        {
            lock (Published)
            { return Published.Where(e => e.Metric == metric).ToArray(); }
        }
    }

    private sealed class RejectingVerifier : ISpeakerVerifier
    {
        public Task<SpeakerVerification> VerifyAsync(
            IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct,
            bool enforceMinSpeech = true) =>
            Task.FromResult(new SpeakerVerification(SpeakerDecision.Rejected, 0.12, null));
    }

    private sealed class IdentifyingVerifier(string name) : ISpeakerVerifier
    {
        public Task<SpeakerVerification> VerifyAsync(
            IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct,
            bool enforceMinSpeech = true) =>
            Task.FromResult(new SpeakerVerification(SpeakerDecision.Accepted, 0.91, name, name));
    }

    // Models the real SpeakerVerifier's short-utterance skip precisely (Skipped when
    // enforceMinSpeech && speechMs < minSpeechMs) without embedding real audio: the peak PCM
    // sample stands in for "known enrolled voice" vs "unknown voice" so a test can drive
    // Accepted/Rejected deterministically by choosing which tone it streams.
    private sealed class GatedToneVerifier(long minSpeechMs, short knownSample) : ISpeakerVerifier
    {
        public Task<SpeakerVerification> VerifyAsync(
            IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct,
            bool enforceMinSpeech = true)
        {
            if (enforceMinSpeech && speechMs < minSpeechMs)
            {
                return Task.FromResult(new SpeakerVerification(SpeakerDecision.Skipped));
            }
            return Task.FromResult(PeakSample(speechAudio) == knownSample
                ? new SpeakerVerification(SpeakerDecision.Accepted, 0.91, "fran", "fran")
                : new SpeakerVerification(SpeakerDecision.Rejected, 0.213, null));
        }

        private static short PeakSample(IReadOnlyList<AudioChunk> chunks)
        {
            short peak = 0;
            foreach (var chunk in chunks)
            {
                var span = chunk.Data.Span;
                for (var i = 0; i + 1 < span.Length; i += 2)
                {
                    var sample = (short)(span[i] | (span[i + 1] << 8));
                    if (Math.Abs((int)sample) > Math.Abs((int)peak))
                    { peak = sample; }
                }
            }
            return peak;
        }
    }

    // Stands in for the agent answering: one reply segment queued, played, and the stream ended.
    // That is the route SendReplyTool takes, so what the test proves and what production does are
    // the same thing — there is no signal-the-turn shortcut to take instead.
    private static void SpeakOneReplySegment(SatelliteSession session)
    {
        session.Turn.BeginSegment().Complete();
        session.Turn.EndStream();
    }

    // Constant-amplitude S16LE. 3200 bytes = 100 ms at 16 kHz mono.
    private static byte[] Pcm(short value, int bytes = 3200)
    {
        var buf = new byte[bytes];
        for (var i = 0; i + 1 < buf.Length; i += 2)
        {
            buf[i] = (byte)(value & 0xFF);
            buf[i + 1] = (byte)((value >> 8) & 0xFF);
        }
        return buf;
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(20);
        }
    }

    private static async Task StopAsync(Task run, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        try
        { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* the run throws or cancels as the link ends */ }
    }

    [Fact]
    public async Task Wake_CommandRunsOnFromTheWakeWord_UsesTheRoomLevelTheSatelliteMeasured()
    {
        // Field report 2026-07-30: "sometimes the voice starts processing when I'm still talking."
        // With no gap after the wake word, the capture's first frames ARE the command, so the
        // gate's noise floor froze at the speaker's own level (6x the room, measured on prod) and
        // the rest of the utterance read as background. The satellite listens to the room the
        // whole time it is idle, so it can report what silence there actually sounds like; the hub
        // caps the floor with it and the command survives.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness
        {
            Wyoming = new WyomingClientSettings
            {
                ReconnectDelaySeconds = 1,
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 10_000,
                MinSpeechMs = 100
            },
            Voice = new VoiceSettings { AgentId = "nabu", FollowUp = new FollowUpSettings { Enabled = false } },
            Config = new SatelliteConfig { Identity = "household", Room = "Office", WakeWord = "ok_nabu" },
            TranscriptText = "baja el volumen al diez por ciento"
        };
        var connection = h.Build("office-01");
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake(new JsonObject
        {
            ["source"] = "wake",
            ["wake_rms"] = 9000.0,
            // Measured on the satellite while idle: no wake word, no capture, just the room.
            ["room_rms"] = 60.0
        });
        h.SendAudio(8000, 4);  // the command starts on the very first frame — no pre-roll gap
        h.SendAudio(2000, 8);  // a quieter clause: 12 dB under the peak, still far above the clamp
        h.SendAudio(0, 20);    // the user actually stops

        var msg = await h.Emitter.FirstAsync(TimeSpan.FromSeconds(10), cts.Token);
        msg.Content.ShouldBe("baja el volumen al diez por ciento");
        // The whole command reached STT: 12 spoken chunks plus the trailing run that ended it. An
        // uncapped floor endpoints inside the quieter clause (or drops the turn as no-speech).
        h.ChunksTranscribed.ShouldBeGreaterThanOrEqualTo(12);

        await StopAsync(run, cts);
    }

    // Only run-pipeline carries the wake metadata; a satellite that also announces its mic stream
    // sends a metadata-free audio-start for the SAME turn. WakeTriggered must report exactly what
    // opened the turn — that field is what RmsOffsetDb is calibrated from, and a wrong value is
    // worse than a missing one because nothing ever fails. Both frame orders are exercised: the
    // canonical one must keep the signal, and the reversed one must not attribute a signal that
    // arrived afterwards to a turn that was already open.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wake_MetadataOnRunPipelineWithAudioStart_AttributesSignalToTheTurnThatOpened(
        bool runPipelineFirst)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness();
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        var format = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };
        var runPipeline = WyomingEvent.Header("run-pipeline", new JsonObject
        {
            ["source"] = "wake",
            ["wake_rms"] = 1234.5,
            ["wake_score"] = 0.87
        });
        // The other announcement of the same turn: a mic-stream open, carrying no wake metadata.
        var audioStart = WyomingEvent.Header("audio-start", format.DeepClone().AsObject());

        h.Send(runPipelineFirst ? runPipeline : audioStart);
        h.Send(runPipelineFirst ? audioStart : runPipeline);

        // Pre-roll gap: real captures open on ambient/gap frames from wake-detection latency,
        // seeding the AdaptiveLevelTracker's noise floor before real speech classifies as speech.
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        await h.Emitter.FirstAsync(TimeSpan.FromSeconds(10), cts.Token);
        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(10));

        var wakes = h.PublishedOf(VoiceMetric.WakeTriggered);
        // One wake for one turn — the second frame must not open another.
        wakes.Length.ShouldBe(1);
        // Canonical order: the announcement reached the opening, so it is reported. Reversed:
        // audio-start already opened the turn with no announcement, and the coordinator's early
        // return discarded the one that arrived afterwards — "unknown" is the only honest answer.
        wakes[0].WakeRms.ShouldBe(runPipelineFirst ? 1234.5 : null);
        wakes[0].WakeScore.ShouldBe(runPipelineFirst ? 0.87 : null);

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task Wake_ThenSilence_ReArmsWithoutWaitingForMaxUtterance()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness
        {
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings
                { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 }
            }
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        // Wake fired but the user says nothing: stream ONLY silence. ~1.2s (12 chunks) exceeds the
        // 800ms no-speech window yet stays well under the 3000ms max-utterance cap.
        h.SendAudio(0, 12);

        // The no-speech window must fire on the wake turn too: re-arm with the closing (empty)
        // transcript instead of holding the mic open until the max-utterance cap, and never dispatch.
        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(8));
        h.WrittenOfType("transcript")[0].Data["text"]!.GetValue<string>().ShouldBe("");
        h.Emitter.Received().Count.ShouldBe(0);

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task Wake_WithoutUtterance_AcknowledgesActiveAlertOnThatSatellite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // STT returns empty so nothing would be dispatched — the ack must come from the wake, not
        // from a dispatch.
        var h = new Harness { TranscriptText = "" };
        using var alertCts = new CancellationTokenSource();
        h.Alerts.Register(new AlertHandle(alertCts, ["kitchen-01"], "test alert", AnnounceKind.Alarm));
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        // Wake fired but no audio data (the user stays silent). A bare wake alone must be enough to
        // dismiss the active alert.
        h.SendWake();

        await UntilAsync(() => alertCts.IsCancellationRequested, TimeSpan.FromSeconds(5));
        alertCts.IsCancellationRequested.ShouldBeTrue();

        await StopAsync(run, cts);
    }

    // The rule the unwind split exists to carry. A satellite whose link just died must stop being an
    // arbitration candidate before anything unbounded runs, or it can still win a wake against a
    // live satellite and silently suppress a real command. The playback loop is exactly that
    // unbounded thing — it can be parked writing to the socket that just died.
    [Fact]
    public async Task Unwind_PlaybackStillDraining_ArbiterRegistrationIsAlreadyReleased()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness();
        h.WriteHook = async (evt, _) =>
        {
            if (evt.Type != "audio-chunk")
            {
                return;
            }
            writeStarted.TrySetResult();
            await parked.Task;
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        await UntilAsync(() => h.Arbiter.IsRegistered("kitchen-01"), TimeSpan.FromSeconds(5));
        connection.Session.Playback.Enqueue(
            new PlaybackJob(
                Label: "reply",
                Kind: PlaybackKind.Reply,
                Priority: AnnouncePriority.Normal,
                Audio: OneChunk()));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        h.DropLink();

        // The link is gone and the playback write is still parked, so the drain cannot have got past
        // it. The registration is already released anyway, because releasing it is not in the drain.
        await UntilAsync(() => !h.Arbiter.IsRegistered("kitchen-01"), TimeSpan.FromSeconds(5));
        run.IsCompleted.ShouldBeFalse();

        parked.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        // And the rest of the unwind still ran once the drain could finish.
        h.Sessions.Get("kitchen-01").ShouldBeNull();
        connection.Session.ControlWriter.ShouldBeNull();
    }

    // The reason a waiting producer no longer needs a token to avoid waiting forever. A satellite
    // that dropped mid-answer used to settle nothing at all: neither the job being played nor
    // anything queued behind it produced a callback of any kind.
    [Fact]
    public async Task Unwind_TornDownMidPlayback_DiscardsTheInFlightJobAndEverythingQueuedBehindIt()
    {
        using var link = new CancellationTokenSource();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness();
        h.WriteHook = (evt, _) =>
        {
            if (evt.Type == "audio-chunk")
            {
                writeStarted.TrySetResult();
            }
            return Task.CompletedTask;
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, link.Token);

        await UntilAsync(() => h.Arbiter.IsRegistered("kitchen-01"), TimeSpan.FromSeconds(5));
        var playing = connection.Session.Playback.Enqueue(
            new PlaybackJob(
                Label: "reply-1",
                Kind: PlaybackKind.Reply,
                Priority: AnnouncePriority.Normal,
                Audio: NeverEndingChunks()));
        var queuedBehind = connection.Session.Playback.Enqueue(
            new PlaybackJob(
                Label: "reply-2",
                Kind: PlaybackKind.Reply,
                Priority: AnnouncePriority.Normal,
                Audio: OneChunk()));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The connection is torn down while one job is mid-audio and another waits behind it, so
        // the loop never reaches a terminal path of its own for either.
        await StopAsync(run, link);

        (await playing.Completed.WaitAsync(TimeSpan.FromSeconds(5)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
        (await queuedBehind.Completed.WaitAsync(TimeSpan.FromSeconds(5)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
    }

    // The link-drop sibling of the teardown above: the run token is NOT cancelled, so the loop is
    // still alive when the drain closes the queue. Every job queued behind the current one must be
    // discarded unplayed — not synthesized and written into the dead socket to settle Failed with
    // one spurious TtsError each — while the in-flight job ends as it really did.
    [Fact]
    public async Task Unwind_LinkDroppedMidPlayback_DiscardsTheQueuedJobsInsteadOfPlayingThemToFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness();
        h.WriteHook = async (evt, _) =>
        {
            if (evt.Type != "audio-chunk")
            {
                return;
            }
            writeStarted.TrySetResult();
            await parked.Task;
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        await UntilAsync(() => h.Arbiter.IsRegistered("kitchen-01"), TimeSpan.FromSeconds(5));
        var playing = connection.Session.Playback.Enqueue(
            new PlaybackJob(
                Label: "reply-1",
                Kind: PlaybackKind.Reply,
                Priority: AnnouncePriority.Normal,
                Audio: OneChunk()));
        var queuedBehind = connection.Session.Playback.Enqueue(
            new PlaybackJob(
                Label: "reply-2",
                Kind: PlaybackKind.Reply,
                Priority: AnnouncePriority.Normal,
                Audio: OneChunk()));
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        // The link drops while reply-1's write is parked the way a dead socket parks one; the drain
        // has closed the queue by the time the write comes back.
        h.DropLink();
        await UntilAsync(() => !h.Arbiter.IsRegistered("kitchen-01"), TimeSpan.FromSeconds(5));
        parked.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        (await playing.Completed.WaitAsync(TimeSpan.FromSeconds(5)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        (await queuedBehind.Completed.WaitAsync(TimeSpan.FromSeconds(5)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Discarded);
        // reply-2 never reached the wire, and no spurious per-job error metric fired.
        h.WrittenOfType("audio-chunk").Count.ShouldBe(1);
        h.PublishedOf(VoiceMetric.TtsError).ShouldBeEmpty();
    }

    // The earcon is what tells the user the mic is open, so the mic must not open until it has been
    // heard. It waits on its job's outcome now instead of a completion source settled by hand from
    // three of five callbacks.
    [Fact]
    public async Task FollowUp_ChimeEnabled_OpensTheMicOnlyAfterTheEarconReachedTheSatellite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var h = new Harness
        {
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings
                { Enabled = true, Chime = true, PlaybackTailMs = 0, WindowMs = 800 }
            }
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        await h.Emitter.ReceivedAtLeastAsync(1, TimeSpan.FromSeconds(10), cts.Token);
        SpeakOneReplySegment(connection.Session);

        await UntilAsync(() => connection.Mic.IsOpen, TimeSpan.FromSeconds(10));

        // The earcon is generated at the satellite's fixed sink rate, which is what tells it apart
        // from the reply audio on the wire. Its envelope is already closed by the time the mic
        // opened, because the mic waits on the outcome that follows it.
        h.WrittenOfType("audio-start")
            .Any(e => e.Data["rate"]!.GetValue<int>() == 22_050).ShouldBeTrue();
        h.WrittenOfType("audio-stop").ShouldNotBeEmpty();

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task Dispatch_Stamp_IsTakenBeforeTheDispatchSoItsOwnWorkIsMeasured()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var h = new Harness { CreateConversationDelay = TimeSpan.FromSeconds(1) };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        (await h.Emitter.FirstAsync(TimeSpan.FromSeconds(20), cts.Token)).Content.ShouldBe("hola");
        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(10));

        long? stamp = null;
        await UntilAsync(() => (stamp ??= connection.Session.Turn.TryConsumeDispatchedAt()) is not null,
            TimeSpan.FromSeconds(5));
        stamp.ShouldNotBeNull();

        // TranscriptDispatcher.DispatchAsync does real work inside: the create_conversation round
        // trip above, the channel/message write, and an awaited Redis publish. Stamping after it
        // returned left all of that in no span at all, on the common path. The stamp must sit
        // immediately after STT so that work is inside AgentRoundTripMs.
        var afterStt = TimeProvider.System.GetElapsedTime(h.SttFinishedAt, stamp!.Value);
        afterStt.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        afterStt.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task Speaker_Conclusive_EmitsIdentifiedPersonAsSender()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness
        {
            // EarlyVerifyMs = 0 keeps the fake off the early-close path; only the terminal verify
            // (which yields the identity) drives this test.
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings { Enabled = false },
                SpeakerVerification = new SpeakerVerificationSettings { EarlyVerifyMs = 0 }
            },
            Verifier = new IdentifyingVerifier("fran")
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        var msg = await h.Emitter.FirstAsync(TimeSpan.FromSeconds(10), cts.Token);
        msg.Content.ShouldBe("hola");
        msg.Sender.ShouldBe("fran"); // conclusive identity routed into Sender, not "household"

        // The gate's verdict must reach the STT chain, not just the message Sender: the decorator's
        // whole TSE policy is keyed off TargetSpeaker, so prove the wiring lands the identified name
        // there (not merely "the floor is positive", which would pin clamping instead of wiring).
        h.SttOptions.ShouldNotBeNull();
        h.SttOptions!.TargetSpeaker.ShouldBe("fran");

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task Speaker_Unknown_RejectsCaptureWithoutSttAndPublishesMetric()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness { Verifier = new RejectingVerifier() };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        // Pre-roll gap: real captures open on ambient/gap frames from wake-detection latency,
        // seeding the AdaptiveLevelTracker's noise floor before real speech classifies as speech.
        h.SendAudio(0, 1);
        // Loud portion is 10 chunks (~1000ms) so capture.Stats.SpeechMs clears the verifier's
        // default 800ms MinVerifySpeechMs — 4 chunks (~400ms) would short-circuit verification to
        // Skipped instead of exercising Rejected.
        h.SendAudio(8000, 10);
        h.SendAudio(0, 6);

        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(10));
        // Closing transcript re-arms the satellite; the conversation ended without STT.
        h.WrittenOfType("transcript")[0].Data["text"]!.GetValue<string>().ShouldBe("");

        h.Stt.Verify(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                            It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()),
            Times.Never());

        var rejection = h.PublishedOf(VoiceMetric.UtteranceRejected).SingleOrDefault();
        rejection.ShouldNotBeNull();
        rejection!.Outcome.ShouldBe("unknown_speaker");
        rejection.Similarity.ShouldBe(0.12);

        // Verification runs before the STT stopwatch starts, so without its own metric the ONNX
        // embedding is invisible latency. The capture here ends long before EarlyVerifyMs (5 s), so
        // only the final inline pass runs — and SpeakerVerifyMs means exactly that pass, the additive
        // one. The early pass has its own member (SpeakerVerifyEarlyMs), asserted in the early-reject
        // test below.
        var verify = h.PublishedOf(VoiceMetric.SpeakerVerifyMs);
        verify.Length.ShouldBe(1);
        verify.ShouldAllBe(e => e.DurationMs != null && e.DurationMs >= 0);
        verify.ShouldAllBe(e => e.Outcome == "final");
        h.PublishedOf(VoiceMetric.SpeakerVerifyEarlyMs).ShouldBeEmpty();

        // The EndpointTailMs PUBLISH SITE, not just the gate underneath it: the satellite streams
        // 6 silent 100 ms frames after the loud ones and TrailingSilenceMs is 200, so the gate ends
        // on the second silent frame and the reported tail must be that 200 — not the 600 ms of
        // silence that kept arriving afterwards. Published even though this capture is rejected,
        // because tuning TrailingSilenceMs needs the rejected captures too.
        var tail = h.PublishedOf(VoiceMetric.EndpointTailMs).SingleOrDefault();
        tail.ShouldNotBeNull();
        tail!.DurationMs.ShouldBe(200);
        tail.EndReason.ShouldBe("trailing_silence");

        h.Emitter.Received().ShouldBeEmpty(); // no message notification reached the agent

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task EarlyMark_NoSpeechYet_KeepsMicOpenForLateSpeech()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        // Small enough to keep the test fast, large enough that the delay below reliably straddles
        // it even under CI jitter.
        const int earlyVerifyMs = 150;
        var h = new Harness
        {
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings
                { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 },
                SpeakerVerification = new SpeakerVerificationSettings { EarlyVerifyMs = earlyVerifyMs }
            },
            Verifier = new GatedToneVerifier(minSpeechMs: 300, knownSample: 8000)
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        // First (wake) utterance: ordinary speech then trailing silence, dispatched normally.
        h.SendWake();
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        // Wake turn dispatched -> simulate the agent's spoken reply so the follow-up window opens.
        await h.Emitter.ReceivedAtLeastAsync(1, TimeSpan.FromSeconds(10), cts.Token);
        SpeakOneReplySegment(connection.Session);
        await UntilAsync(() => connection.Mic.IsOpen, TimeSpan.FromSeconds(10));

        // Follow-up mic reopened and gets pure ambient noise — nobody has started speaking yet.
        // Real wall-clock delay (not more gate-time audio) so the early-verify mark elapses while
        // the capture's gate-classified speech is still exactly zero — this is the production
        // symptom (speechMs=0 at the early mark).
        h.SendAudio(0, 1);
        await Task.Delay(TimeSpan.FromMilliseconds(earlyVerifyMs + 450), cts.Token);

        // The user starts talking only now, well after the early mark has already elapsed.
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        // The late-arriving speech must still reach the agent: a capture with zero gate-classified
        // speech at the early mark must never be early-rejected, so the mic stays open for the
        // speaker who is about to talk.
        await h.Emitter.ReceivedAtLeastAsync(2, TimeSpan.FromSeconds(15), cts.Token);
        h.Emitter.Received().Count.ShouldBe(2);

        h.PublishedOf(VoiceMetric.UtteranceRejected)
            .Any(e => e.Outcome == "unknown_speaker_early").ShouldBeFalse();

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task EarlyMark_ContinuousUnknownVoice_StillEarlyRejects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const int earlyVerifyMs = 150;
        var h = new Harness
        {
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings { Enabled = false },
                SpeakerVerification = new SpeakerVerificationSettings { EarlyVerifyMs = earlyVerifyMs }
            },
            Verifier = new GatedToneVerifier(minSpeechMs: 300, knownSample: 8000)
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        // Continuous unknown-voice audio (a tone distinct from the enrolled one) latches as speech
        // and never stops on its own — models a talking TV holding the capture open. No trailing
        // silence is sent, so the capture is still open purely because wall-clock time passes.
        h.SendAudio(0, 1);
        h.SendAudio(3000, 4);

        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(10));
        // Closing transcript re-arms the satellite; the conversation ended without STT.
        h.WrittenOfType("transcript")[0].Data["text"]!.GetValue<string>().ShouldBe("");

        h.Stt.Verify(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                            It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()),
            Times.Never());

        var rejection = h.PublishedOf(VoiceMetric.UtteranceRejected).SingleOrDefault();
        rejection.ShouldNotBeNull();
        rejection!.Outcome.ShouldBe("unknown_speaker_early");
        // The "TV" DID latch as speech, unlike the zero-speech scenario above.
        (rejection.SpeechMs ?? 0).ShouldBeGreaterThanOrEqualTo(300L);

        // This is the only test that forces the early-close branch to reject, so it is the one place
        // the early pass gets its own coverage. It must publish SpeakerVerifyEarlyMs, NOT
        // SpeakerVerifyMs: the early pass runs while the capture is still open, concurrent with the
        // user speaking, so it overlaps the utterance and is not part of the additive decomposition.
        // Sharing one member let any grouping not keyed on Outcome (the dashboard defaults to
        // SatelliteId) silently average an overlapping span together with an additive one.
        var verify = h.PublishedOf(VoiceMetric.SpeakerVerifyEarlyMs).SingleOrDefault();
        verify.ShouldNotBeNull();
        verify!.Outcome.ShouldBe("early");
        (verify.DurationMs ?? -1).ShouldBeGreaterThanOrEqualTo(0L);
        verify.Similarity.ShouldBe(0.213); // GatedToneVerifier's fixed Rejected similarity
        h.PublishedOf(VoiceMetric.SpeakerVerifyMs).ShouldBeEmpty();

        h.Emitter.Received().ShouldBeEmpty(); // no message notification reached the agent

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task FollowUp_Enabled_DispatchesFollowUpWithoutSecondWake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var h = new Harness
        {
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings
                { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 }
            }
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        // First utterance: speech then trailing silence. Leading silent chunk seeds the
        // AdaptiveLevelTracker's noise floor (models the real pre-roll gap).
        h.SendWake();
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        // First utterance dispatched -> simulate the agent's spoken reply so the follow-up opens.
        await h.Emitter.ReceivedAtLeastAsync(1, TimeSpan.FromSeconds(10), cts.Token);
        h.WrittenOfType("transcript").ShouldBeEmpty(); // transcript deferred (no re-arm yet)
        SpeakOneReplySegment(connection.Session);

        // The follow-up window opens asynchronously after the reply signal; wait for the capture to
        // become active, then stream the wake-free follow-up utterance into it. A fresh
        // SilenceGate+AdaptiveLevelTracker is opened per capture, so this needs its own leading
        // silent chunk to seed the floor.
        await UntilAsync(() => connection.Mic.IsOpen, TimeSpan.FromSeconds(10));
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        // Second utterance must be dispatched WITHOUT a second run-pipeline (wake-free follow-up).
        await h.Emitter.ReceivedAtLeastAsync(2, TimeSpan.FromSeconds(15), cts.Token);
        h.Emitter.Received().Count.ShouldBe(2);

        await StopAsync(run, cts);
    }

    [Fact]
    public async Task FollowUp_Silence_ReArmsSatelliteWithClosingTranscript()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var h = new Harness
        {
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings
                { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 }
            }
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        // First utterance dispatched -> simulate the agent's spoken reply so the follow-up opens.
        await h.Emitter.ReceivedAtLeastAsync(1, TimeSpan.FromSeconds(10), cts.Token);
        h.WrittenOfType("transcript").ShouldBeEmpty(); // transcript deferred (no re-arm yet)
        SpeakOneReplySegment(connection.Session);

        // Wait for the wake-free window to open, then stream pure silence into it: 12 silent chunks
        // (~1.2s) exceed the 800ms no-speech window.
        await UntilAsync(() => connection.Mic.IsOpen, TimeSpan.FromSeconds(10));
        h.SendAudio(0, 12);

        // The no-speech timeout fires -> the closing (empty) transcript re-arms the satellite.
        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(15));
        h.WrittenOfType("transcript")[0].Data["text"]!.GetValue<string>().ShouldBe("");
        h.Emitter.Received().Count.ShouldBe(1); // the silent follow-up was never dispatched

        await StopAsync(run, cts);
    }

    // The host's reconnect loop is built on the run throwing: a link that dies has to surface as an
    // exception here, not as a quiet return that would leave the loop believing the satellite is
    // still connected.
    [Fact]
    public async Task Run_InboundStreamFaults_ThrowsSoTheReconnectLoopRetries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness();
        var connection = h.Build();

        var run = connection.RunAsync(Failing(), cts.Token);

        await Should.ThrowAsync<IOException>(() => run);
        // Unwound on the way out, even though the read loop left by an exception.
        h.Sessions.Get("kitchen-01").ShouldBeNull();
        h.Arbiter.IsRegistered("kitchen-01").ShouldBeFalse();
    }

    private static async IAsyncEnumerable<WyomingEvent> Failing()
    {
        await Task.Yield();
        throw new IOException("satellite connection dropped");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    // Audio that never ends on its own, so the job is still in flight when the link drops.
    private static async IAsyncEnumerable<AudioChunk> NeverEndingChunks(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            yield return new AudioChunk
            {
                Data = Pcm(1000),
                Format = new AudioFormat { SampleRateHz = 22_050, SampleWidthBytes = 2, Channels = 1 }
            };
            await Task.Delay(50, ct);
        }
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunk()
    {
        await Task.Yield();
        yield return new AudioChunk
        {
            Data = Pcm(1000),
            Format = new AudioFormat { SampleRateHz = 22_050, SampleWidthBytes = 2, Channels = 1 }
        };
    }
}