using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The point of the turn-decomposition metrics is that they TILE the wait: SpeechEndToFirstAudioMs
// must equal the sum of its named parts, or the headline number is quietly wrong and whoever reads
// the dashboard goes hunting for a span that does not exist. Nothing asserted that relationship
// while the metrics were being added, which is exactly how the speech-end anchor came to be a whole
// trailing-silence run late.
public class TurnLatencyDecompositionTests
{
    private const int ChunkBytes = 3200; // 100 ms at 16 kHz / 16-bit / mono
    private static readonly TimeSpan _chunkDuration = TimeSpan.FromMilliseconds(100);

    // One synthetic turn's stage lengths. Speech and tail are audio-domain (the gate derives them
    // from PCM frame durations); the rest are wall-clock.
    private const int SpeechMs = 1500;
    private const int EndpointTailMs = 200;
    private const int VerifyMs = 120;
    private const int SttMs = 900;
    private const int AgentMs = 2500;
    private const int QueueWaitMs = 400;
    private const int TtsMs = 300;

    private readonly ArmedClock _clock = new(DateTimeOffset.UtcNow);
    private readonly SatelliteSession _session;

    // Standing in for CaptureSession: the turn anchors belong to ITurnAnchor, not to the
    // playback queue every producer holds.
    private ITurnAnchor Anchors => _session.Playback;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly string _conversationId;
    private readonly ReplySpeaker _speaker;
    private readonly List<VoiceEvent> _published = [];

    public TurnLatencyDecompositionTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-kitchen");
                var topic = new TopicMetadata("topic-kitchen", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        var manager = new VoiceConversationManager(
            factory.Object, _accumulator, _clock, TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance);
        _conversationId = manager.GetOrCreateAsync(_session, "agent-1", "hello", default).GetAwaiter().GetResult();

        // Synthesis is lazy and pulled inside the playback loop, so this is where TTS time-to-first
        // -audio happens — the same place the loop measures it.
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => SynthesizeAsync());

        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is VoiceEvent v)
                {
                    lock (_published)
                    { _published.Add(v); }
                }
            });

        _speaker = new ReplySpeaker(
            _accumulator, tts.Object, new VoiceSettings(), metrics.Object, _clock,
            NullLogger<ReplySpeaker>.Instance);
    }

    private void Say(string content, ReplyContentType contentType, bool isComplete) =>
        _speaker.SpeakUtteranceReply(_session, new SendReplyParams
        {
            ConversationId = _conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete
        });

    private async IAsyncEnumerable<AudioChunk> SynthesizeAsync()
    {
        _clock.Advance(TimeSpan.FromMilliseconds(TtsMs));
        yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
        await Task.CompletedTask;
    }

    [Fact]
    public async Task VoiceTurn_SpeechEndToFirstAudio_EqualsTheSumOfItsNamedSpans()
    {
        // Walks one turn on a single FakeTimeProvider, feeding a real SilenceGate/UtteranceCapture so
        // the endpoint tail is the gate's own value, and driving the real ReplySpeaker so the round
        // trip / queue wait / TTS / speech-end spans are the real published ones.
        //
        // Encoded gap: SpeakerVerifyMs and SttLatencyMs are published by WyomingSatelliteHost, which
        // is not reachable from a unit test, and they are timed around exactly the operations this
        // test advances the clock for. They therefore enter the sum as the declared VerifyMs/SttMs
        // advances rather than as published events. Every other span is asserted against what the
        // production code actually published, and every millisecond advanced between speech end and
        // first audio is claimed by exactly one term — which is the property under test.
        var capture = _session.Mic.Open(NewGate());
        Anchors.MarkTurnStart(_clock.GetTimestamp());

        // Audio arrives in real time, so advance the clock in step with each frame's duration: that
        // equivalence between audio-domain and wall-clock time is what lets the tail be rewound.
        FeedPacedAudio(capture, Silent()); // pre-roll gap seeds the adaptive floor
        Enumerable.Range(0, SpeechMs / 100).ToList().ForEach(_ => FeedPacedAudio(capture, Loud()));
        Enumerable.Range(0, EndpointTailMs / 100).ToList().ForEach(_ => FeedPacedAudio(capture, Silent()));

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.TrailingSilenceMs.ShouldBe(EndpointTailMs);

        _session.Mic.Close(capture);
        Anchors.MarkSpeechEnd(_clock.GetTimestamp(), capture.Stats.TrailingSilenceMs, _clock);

        _clock.Advance(TimeSpan.FromMilliseconds(VerifyMs)); // final speaker-verify pass (ONNX embed)
        _clock.Advance(TimeSpan.FromMilliseconds(SttMs));    // whisper decode

        _session.Turn.Reset();
        _session.Turn.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromMilliseconds(AgentMs)); // the agent process thinking

        Say("listo", ReplyContentType.Text, false);
        Say("", ReplyContentType.StreamComplete, true);

        _clock.Advance(TimeSpan.FromMilliseconds(QueueWaitMs)); // the reply waits behind another job

        using var run = new CancellationTokenSource();
        var pump = _session.Playback.RunAsync(async (_, _) => await Task.Yield(), run.Token, _clock);
        await _clock.WaitForAnyLiveAsync();          // the loop reached the playback-drain wait
        _clock.Advance(TimeSpan.FromSeconds(1));     // drain the remaining playback duration
        await _session.Turn.AwaitSpoken().WaitAsync(TimeSpan.FromSeconds(5));
        await run.StopAsync(pump);

        var roundTrip = Duration(VoiceMetric.AgentRoundTripMs);
        var queueWait = Duration(VoiceMetric.ReplyQueueWaitMs);
        var tts = Duration(VoiceMetric.TtsLatencyMs);
        var whole = Duration(VoiceMetric.SpeechEndToFirstAudioMs);

        roundTrip.ShouldBe(AgentMs);

        // Prefetch starts a reply segment's synthesis at enqueue, so on a turn that waits behind
        // another job the synthesis happens DURING that wait: the queue-wait span absorbs it and the
        // loop-observed TTS span goes to zero. The pair is conserved, which is what keeps the
        // decomposition additive. TtsLatencyMs still reports real synthesis time on the common path,
        // where the loop is idle and pulls the moment the job is queued.
        (queueWait + tts).ShouldBe(QueueWaitMs + TtsMs);
        queueWait.ShouldBe(QueueWaitMs + TtsMs);
        tts.ShouldBe(0);

        // The sum must close exactly: on a FakeTimeProvider nothing elapses that the test did not
        // advance, so any residual is a real unattributed (or double-counted) span, not jitter.
        (capture.Stats.TrailingSilenceMs + VerifyMs + SttMs + roundTrip + queueWait + tts)
            .ShouldBe(whole);
        whole.ShouldBe(EndpointTailMs + VerifyMs + SttMs + AgentMs + QueueWaitMs + TtsMs);

        // WakeToFirstAudioMs must still be the same span plus the user's own speech, so the two
        // stay comparable and the difference is exactly what the user said.
        Duration(VoiceMetric.WakeToFirstAudioMs).ShouldBe(whole + SpeechMs + 100 /* pre-roll gap */);
    }

    private void FeedPacedAudio(UtteranceCapture capture, AudioChunk chunk)
    {
        capture.Feed(chunk);
        _clock.Advance(_chunkDuration);
    }

    private long Duration(VoiceMetric metric)
    {
        var evt = _published.SingleOrDefault(e => e.Metric == metric);
        evt.ShouldNotBeNull($"expected exactly one {metric} sample");
        evt!.DurationMs.ShouldNotBeNull();
        return evt.DurationMs!.Value;
    }

    private static SilenceGate NewGate() => new(
        new AdaptiveLevelTracker(
            clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
            floorWindow: TimeSpan.FromSeconds(3)),
        trailingSilence: TimeSpan.FromMilliseconds(EndpointTailMs),
        maxUtterance: TimeSpan.FromMilliseconds(5000),
        minSpeech: TimeSpan.FromMilliseconds(100));

    private static AudioChunk Loud()
    {
        var pcm = new byte[ChunkBytes];
        Enumerable.Range(0, ChunkBytes / 2).ToList().ForEach(i =>
        { pcm[i * 2] = 0x40; pcm[(i * 2) + 1] = 0x1F; });
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static AudioChunk Silent() =>
        new() { Data = new byte[ChunkBytes], Format = AudioFormat.WyomingStandard };
}