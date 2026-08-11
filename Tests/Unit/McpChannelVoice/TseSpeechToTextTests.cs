using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Tse;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseSpeechToTextTests
{
    private sealed class RecordingInner : ISpeechToText
    {
        public byte[]? ReceivedPayload;
        public TranscriptionOptions? ReceivedOptions;
        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            var buffer = new List<byte>();
            await foreach (var chunk in audio.WithCancellation(ct))
            {
                buffer.AddRange(chunk.Data.ToArray());
            }
            ReceivedPayload = buffer.ToArray();
            ReceivedOptions = options;
            return new TranscriptionResult { Text = "ok" };
        }
    }

    private sealed class StubClient(TseExtractReply reply) : ITseExtractorClient
    {
        public StubClient(byte[]? wav) : this(new TseExtractReply(wav, Rejected: false)) { }
        public (byte[] Wav, string Speaker)? LastCall;
        public Task<TseExtractReply> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct)
        {
            LastCall = (mixtureWav, speaker);
            return Task.FromResult(reply);
        }
    }

    private sealed class ThrowingClient(Exception exception) : ITseExtractorClient
    {
        public Task<TseExtractReply> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct) =>
            throw exception;
    }

    private sealed class RecordingMetrics : IMetricsPublisher
    {
        public readonly List<VoiceEvent> Events = [];

        public void Publish(MetricEvent metricEvent)
        {
            if (metricEvent is VoiceEvent voice)
            {
                Events.Add(voice);
            }
        }

    }

    // Preserves frame boundaries (unlike RecordingInner, which flattens to one byte blob) so
    // re-chunking tests can assert on individual frame lengths and ordering.
    private sealed class FrameRecordingInner : ISpeechToText
    {
        public readonly List<byte[]> ReceivedFrames = [];
        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            await foreach (var chunk in audio.WithCancellation(ct))
            {
                ReceivedFrames.Add(chunk.Data.ToArray());
            }
            return new TranscriptionResult { Text = "ok" };
        }
    }

    // Terminal STT for the SegmentedSpeechToText seam test: reports non-empty text whenever it
    // is invoked at all, so the assertion isolates SegmentedSpeechToText's own segment-or-not
    // decision (the thing the bug breaks) rather than any transcription content.
    private sealed class CountingInner : ISpeechToText
    {
        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            var count = 0;
            await foreach (var _ in audio.WithCancellation(ct))
            {
                count++;
            }
            return new TranscriptionResult { Text = count > 0 ? "speech" : "" };
        }
    }

    private static readonly byte[] _rawPcm = [1, 2, 3, 4, 5, 6];

    private static async IAsyncEnumerable<AudioChunk> Chunks()
    {
        yield return new AudioChunk { Data = _rawPcm, Format = AudioFormat.WyomingStandard };
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<AudioChunk> ChunksFrom(IEnumerable<AudioChunk> parts)
    {
        foreach (var chunk in parts)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }

    private static ISpeechToText BuildStt(
        ISpeechToText inner, TseMode mode, byte[]? clientReply, TseAuditTrail? audit = null) =>
        TseSpeechToText.Wrap(
            inner, new TseSettings { Mode = mode, NoiseFloorThreshold = 400 }, new StubClient(clientReply),
            audit ?? new TseAuditTrail(null, 1, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance),
            new RecordingMetrics(), NullLoggerFactory.Instance);

    private static TranscriptionOptions Options(string? speaker = "Dethon", double? floor = 900) =>
        new()
        {
            TargetSpeaker = speaker,
            NoiseFloorRms = floor,
            Language = "es",
            SatelliteId = "office-01",
            Room = "Office",
            Identity = "household"
        };

    private static (ISpeechToText Stt, RecordingInner Inner, StubClient Client, RecordingMetrics Metrics) Build(
        TseMode mode, byte[]? clientReply)
    {
        var inner = new RecordingInner();
        var client = new StubClient(clientReply);
        var metrics = new RecordingMetrics();
        var audit = new TseAuditTrail(null, 1, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance);
        var settings = new TseSettings { Mode = mode, NoiseFloorThreshold = 400 };
        var stt = TseSpeechToText.Wrap(inner, settings, client, audit, metrics, NullLoggerFactory.Instance);
        return (stt, inner, client, metrics);
    }

    [Fact]
    public async Task AutoWithoutSpeakerSkipsToRaw()
    {
        var (stt, inner, client, metrics) = Build(TseMode.Auto, clientReply: null);
        await stt.TranscribeAsync(Chunks(), Options(speaker: null), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(_rawPcm);
        client.LastCall.ShouldBeNull();
        metrics.Events.ShouldHaveSingleItem().Metric.ShouldBe(VoiceMetric.TseSkipped);
        metrics.Events[0].Outcome.ShouldBe("no_speaker");
    }

    [Fact]
    public async Task AutoQuietFloorSkipsToRaw()
    {
        var (stt, inner, client, metrics) = Build(TseMode.Auto, clientReply: null);
        await stt.TranscribeAsync(Chunks(), Options(floor: 100), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(_rawPcm);
        client.LastCall.ShouldBeNull();
        var evt = metrics.Events.ShouldHaveSingleItem();
        evt.Metric.ShouldBe(VoiceMetric.TseSkipped);
        evt.Outcome.ShouldBe("quiet");
    }

    [Fact]
    public async Task AutoNoisyFailureFallsBackToRaw()
    {
        var (stt, inner, client, metrics) = Build(TseMode.Auto, clientReply: null);
        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(_rawPcm);
        client.LastCall.ShouldNotBeNull();
        var evt = metrics.Events.ShouldHaveSingleItem();
        evt.Metric.ShouldBe(VoiceMetric.TseFailed);
        evt.Outcome.ShouldBe("unavailable");
    }

    [Fact]
    public async Task AutoNoisySuccessFeedsExtractedAudioToInner()
    {
        var extractedPcm = new byte[] { 40, 41, 42, 43 };
        var reply = WavCodec.Encode([new AudioChunk { Data = extractedPcm, Format = AudioFormat.WyomingStandard }]);
        var (stt, inner, client, metrics) = Build(TseMode.Auto, reply);
        var result = await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        result.Text.ShouldBe("ok");
        inner.ReceivedPayload.ShouldBe(extractedPcm);
        inner.ReceivedOptions!.Language.ShouldBe("es");
        client.LastCall!.Value.Speaker.ShouldBe("Dethon");
        WavCodec.Decode(client.LastCall.Value.Wav).Data.ToArray().ShouldBe(_rawPcm);
        metrics.Events.Select(e => e.Metric).ShouldBe([VoiceMetric.TseInvoked, VoiceMetric.TseLatencyMs]);
        metrics.Events[0].Speaker.ShouldBe("Dethon");
    }

    [Fact]
    public async Task AlwaysModeIgnoresFloor()
    {
        var reply = WavCodec.Encode([new AudioChunk { Data = new byte[] { 7 }, Format = AudioFormat.WyomingStandard }]);
        var (stt, inner, client, _) = Build(TseMode.Always, reply);
        await stt.TranscribeAsync(Chunks(), Options(floor: 0), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(new byte[] { 7 });
        client.LastCall.ShouldNotBeNull();
    }

    [Fact]
    public async Task RejectedReplyFallsBackToRawWithRejectedOutcome()
    {
        var inner = new RecordingInner();
        var metrics = new RecordingMetrics();
        var stt = TseSpeechToText.Wrap(
            inner, new TseSettings { Mode = TseMode.Auto, NoiseFloorThreshold = 400 },
            new StubClient(new TseExtractReply(Wav: null, Rejected: true)),
            new TseAuditTrail(null, 1, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance),
            metrics, NullLoggerFactory.Instance);

        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);

        inner.ReceivedPayload.ShouldBe(_rawPcm);
        var evt = metrics.Events.ShouldHaveSingleItem();
        evt.Metric.ShouldBe(VoiceMetric.TseFailed);
        evt.Outcome.ShouldBe("rejected");
    }

    [Fact]
    public async Task GarbageReplyFallsBackToRaw()
    {
        var (stt, inner, _, metrics) = Build(TseMode.Auto, clientReply: [1, 2, 3]); // not RIFF
        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(_rawPcm);
        var evt = metrics.Events.ShouldHaveSingleItem();
        evt.Metric.ShouldBe(VoiceMetric.TseFailed);
        evt.Outcome.ShouldBe("malformed");
    }

    [Fact]
    public async Task PublishedEventsCarrySatelliteAttribution()
    {
        var reply = WavCodec.Encode([new AudioChunk { Data = new byte[] { 7 }, Format = AudioFormat.WyomingStandard }]);
        var (stt, _, _, metrics) = Build(TseMode.Auto, reply);
        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        metrics.Events.ShouldNotBeEmpty();
        metrics.Events.ShouldAllBe(e => e.SatelliteId == "office-01" && e.Room == "Office");
    }

    [Fact]
    public async Task PublishedEventsNameTheSatelliteIdentity_NotTheTargetSpeaker()
    {
        // Identity means the satellite's configured identity on every other voice event (the
        // dispatcher says so where it routes an enrolled speaker into the sender instead). This was
        // the one publisher writing the target speaker there, so a dashboard grouped by identity
        // mixed satellites and people. The speaker has its own field.
        var reply = WavCodec.Encode([new AudioChunk { Data = new byte[] { 7 }, Format = AudioFormat.WyomingStandard }]);
        var (stt, _, _, metrics) = Build(TseMode.Auto, reply);

        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);

        metrics.Events.ShouldNotBeEmpty();
        metrics.Events.ShouldAllBe(e => e.Identity == "household" && e.Speaker == "Dethon");
    }

    [Fact]
    public async Task EmptyExtractionReplyFallsBackToRaw()
    {
        var reply = WavCodec.Encode([]); // well-formed RIFF, zero-length data chunk
        var (stt, inner, _, metrics) = Build(TseMode.Auto, reply);
        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(_rawPcm);
        var evt = metrics.Events.ShouldHaveSingleItem();
        evt.Metric.ShouldBe(VoiceMetric.TseFailed);
        evt.Outcome.ShouldBe("empty");
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var inner = new RecordingInner();
        var client = new ThrowingClient(new OperationCanceledException());
        var metrics = new RecordingMetrics();
        var audit = new TseAuditTrail(null, 1, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance);
        var settings = new TseSettings { Mode = TseMode.Auto, NoiseFloorThreshold = 400 };
        var stt = TseSpeechToText.Wrap(inner, settings, client, audit, metrics, NullLoggerFactory.Instance);

        await Should.ThrowAsync<OperationCanceledException>(
            () => stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None));

        inner.ReceivedPayload.ShouldBeNull();
        metrics.Events.ShouldBeEmpty();
    }

    // The seam the task-level fakes always paper over: a REAL SegmentedSpeechToText as the
    // inner backend. WavCodec.Decode hands the extraction reply back as one giant chunk; fed
    // straight in, SegmentedSpeechToText's fresh SilenceGate/AdaptiveLevelTracker sees a single
    // frame, whose smoothing/min-window each hold exactly that one entry -- so the frame becomes
    // its own noise floor and IsSpeech can never clear floor + EnterMarginDb. SpeechElapsed stays
    // zero, no segment is ever added, and the whole successful extraction transcribes as "".
    [Fact]
    public async Task AutoNoisySuccess_WithRealSegmentedSttInner_ProducesNonEmptyTranscript()
    {
        const int chunkBytes = 3200; // 100 ms @ 16 kHz/16-bit/mono -- matches SegmentedSpeechToTextTests' cadence

        static byte[] loud()
        {
            var pcm = new byte[chunkBytes];
            for (var i = 0; i < pcm.Length; i += 2)
            {
                pcm[i] = 0x40;
                pcm[i + 1] = 0x1F; // Int16 8000 little-endian => RMS >> 500 (SilenceRmsThreshold)
            }
            return pcm;
        }

        // 10 dummy 100 ms chunks fix the capture's frame cadence for re-chunking; their content
        // is irrelevant here -- only Rechunk's slice LENGTHS come from them. The real speech-like
        // content lives entirely in the sidecar's "extracted" reply below.
        var chunks = Enumerable.Range(0, 10)
            .Select(_ => new AudioChunk { Data = new byte[chunkBytes], Format = AudioFormat.WyomingStandard })
            .ToArray();

        // 1 silent frame seeds the adaptive floor, then 9 loud frames (900 ms, clearing the
        // 800 ms MinSegmentMs default) -- the same speech/silence synthesis SegmentedSpeechToTextTests
        // uses, reused here rather than inventing a new one.
        var extractedPcm = new byte[chunkBytes].Concat(Enumerable.Range(0, 9).SelectMany(_ => loud())).ToArray();
        var reply = WavCodec.Encode([new AudioChunk { Data = extractedPcm, Format = AudioFormat.WyomingStandard }]);

        var segmented = SegmentedSpeechToText.Wrap(
            new CountingInner(), new SegmentedSttConfig { Enabled = true }, new WyomingClientSettings(),
            NullLoggerFactory.Instance);
        var stt = BuildStt(segmented, TseMode.Auto, reply);

        var result = await stt.TranscribeAsync(ChunksFrom(chunks), Options(), CancellationToken.None);

        result.Text.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SuccessPath_ShorterExtractedAudio_RechunksToExactlyExtractedBytes()
    {
        var originalChunks = new[]
        {
            new AudioChunk { Data = new byte[] { 10, 11, 12, 13 }, Format = AudioFormat.WyomingStandard },
            new AudioChunk { Data = new byte[] { 20, 21, 22, 23 }, Format = AudioFormat.WyomingStandard },
            new AudioChunk { Data = new byte[] { 30, 31, 32, 33 }, Format = AudioFormat.WyomingStandard }
        };
        var extractedPcm = new byte[] { 100, 101, 102, 103, 104, 105 }; // 6 bytes: shorter than the 12-byte mixture
        var reply = WavCodec.Encode([new AudioChunk { Data = extractedPcm, Format = AudioFormat.WyomingStandard }]);
        var inner = new FrameRecordingInner();
        var stt = BuildStt(inner, TseMode.Auto, reply);

        await stt.TranscribeAsync(ChunksFrom(originalChunks), Options(), CancellationToken.None);

        inner.ReceivedFrames.Select(f => f.Length).ShouldBe([4, 2]); // stops once extracted is exhausted
        inner.ReceivedFrames.SelectMany(f => f).ToArray().ShouldBe(extractedPcm); // nothing lost or duplicated
    }

    [Fact]
    public async Task SuccessPath_ExtractedAudioNotAnExactMultiple_RechunksToExactlyExtractedBytes()
    {
        var originalChunks = new[]
        {
            new AudioChunk { Data = new byte[] { 10, 11, 12, 13 }, Format = AudioFormat.WyomingStandard },
            new AudioChunk { Data = new byte[] { 20, 21, 22, 23 }, Format = AudioFormat.WyomingStandard },
            new AudioChunk { Data = new byte[] { 30, 31, 32, 33 }, Format = AudioFormat.WyomingStandard }
        };
        var extractedPcm = Enumerable.Range(100, 10).Select(b => (byte)b).ToArray(); // 10 bytes: not a multiple of 4
        var reply = WavCodec.Encode([new AudioChunk { Data = extractedPcm, Format = AudioFormat.WyomingStandard }]);
        var inner = new FrameRecordingInner();
        var stt = BuildStt(inner, TseMode.Auto, reply);

        await stt.TranscribeAsync(ChunksFrom(originalChunks), Options(), CancellationToken.None);

        inner.ReceivedFrames.Select(f => f.Length).ShouldBe([4, 4, 2]);
        inner.ReceivedFrames.SelectMany(f => f).ToArray().ShouldBe(extractedPcm);
    }

    [Fact]
    public async Task SuccessPath_RecordsMixtureAndExtractedAudioDistinctlyToTheAuditTrail()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tse-audit-decorator-{Guid.NewGuid():N}");
        try
        {
            var extractedPcm = new byte[] { 40, 41, 42, 43 };
            var reply = WavCodec.Encode([new AudioChunk { Data = extractedPcm, Format = AudioFormat.WyomingStandard }]);
            var client = new StubClient(reply);
            var audit = new TseAuditTrail(dir, 10, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance);
            var settings = new TseSettings { Mode = TseMode.Auto, NoiseFloorThreshold = 400 };
            var stt = TseSpeechToText.Wrap(
                new RecordingInner(), settings, client, audit, new RecordingMetrics(), NullLoggerFactory.Instance);

            await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);

            var pairDir = Directory.GetDirectories(dir).ShouldHaveSingleItem();
            var mixtureOnDisk = File.ReadAllBytes(Path.Combine(pairDir, "mixture.wav"));
            var extractedOnDisk = File.ReadAllBytes(Path.Combine(pairDir, "extracted.wav"));

            mixtureOnDisk.ShouldBe(client.LastCall!.Value.Wav); // exact bytes sent to the sidecar
            mixtureOnDisk.ShouldNotBe(reply); // sanity: mixture and extracted really are distinct payloads
            extractedOnDisk.ShouldBe(reply); // the raw sidecar reply, not the decoded/re-chunked PCM
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SkipAndFailurePaths_NeverWriteToTheAuditTrail()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tse-audit-decorator-{Guid.NewGuid():N}");
        try
        {
            var audit = new TseAuditTrail(dir, 10, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance);

            await BuildStt(new RecordingInner(), TseMode.Auto, null, audit)
                .TranscribeAsync(Chunks(), Options(speaker: null), CancellationToken.None); // no_speaker skip
            await BuildStt(new RecordingInner(), TseMode.Auto, null, audit)
                .TranscribeAsync(Chunks(), Options(floor: 100), CancellationToken.None); // quiet skip
            await BuildStt(new RecordingInner(), TseMode.Auto, null, audit)
                .TranscribeAsync(Chunks(), Options(), CancellationToken.None); // sidecar unavailable
            await BuildStt(new RecordingInner(), TseMode.Auto, [1, 2, 3], audit)
                .TranscribeAsync(Chunks(), Options(), CancellationToken.None); // malformed reply
            await BuildStt(new RecordingInner(), TseMode.Auto, WavCodec.Encode([]), audit)
                .TranscribeAsync(Chunks(), Options(), CancellationToken.None); // empty reply

            Directory.Exists(dir).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}