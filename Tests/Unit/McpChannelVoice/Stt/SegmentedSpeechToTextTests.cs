using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class SegmentedSpeechToTextTests
{
    private const int ChunkBytes = 3200; // 100 ms @ 16 kHz/16-bit/mono

    private static byte[] LoudPcm()
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = 0x40;     // Int16 8000 little-endian => RMS >> 500
            pcm[i + 1] = 0x1F;
        }
        return pcm;
    }

    private static AudioChunk Loud() => new()
    { Data = LoudPcm(), Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };

    private static AudioChunk Silent() => new()
    { Data = new byte[ChunkBytes], Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };

    private static IEnumerable<AudioChunk> Speech(int chunks) => Enumerable.Range(0, chunks).Select(_ => Loud());
    private static IEnumerable<AudioChunk> Silence(int chunks) => Enumerable.Range(0, chunks).Select(_ => Silent());

    private static async IAsyncEnumerable<AudioChunk> Stream(params IEnumerable<AudioChunk>[] parts)
    {
        foreach (var chunk in parts.SelectMany(p => p))
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    private static AudioChunk Tone(short amplitude)
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };
    }

    private static IEnumerable<AudioChunk> Babble(int chunks) =>
        Enumerable.Range(0, chunks).Select(_ => Tone(2000));

    // 100 ms chunks: 300 ms segment-silence => 3 silent chunks close a segment;
    // 500 ms min-segment => 5 loud chunks minimum.
    private static SegmentedSttConfig Config(int maxInFlight = 1, int firstSplitAfterMs = 0) => new()
    {
        Enabled = true,
        SilenceRmsThreshold = 500,
        SegmentSilenceMs = 300,
        MinSegmentMs = 500,
        MaxInFlightDecodes = maxInFlight,
        FirstSplitAfterMs = firstSplitAfterMs
    };

    private static SegmentedSpeechToText New(ISpeechToText inner, SegmentedSttConfig? config = null) =>
        new(inner, config ?? Config(), new WyomingClientSettings(), NullLogger<SegmentedSpeechToText>.Instance);

    // Inner stub: returns the chunk count it received as text, optionally via a custom handler.
    private sealed class FakeStt(Func<int, Task<TranscriptionResult>>? handler = null) : ISpeechToText
    {
        private readonly Lock _lock = new();
        private int _concurrent;
        public int MaxConcurrent { get; private set; }
        public int Calls { get; private set; }

        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            lock (_lock)
            { _concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, _concurrent); Calls++; }
            try
            {
                var count = 0;
                await foreach (var _ in audio.WithCancellation(ct))
                {
                    count++;
                }

                return handler is null
                    ? new TranscriptionResult { Text = count.ToString() }
                    : await handler(count);
            }
            finally { lock (_lock) { _concurrent--; } }
        }
    }

    [Fact]
    public async Task TranscribeAsync_NoPause_DecodesWholeUtteranceOnce()
    {
        var inner = new FakeStt();

        // Leading Silence(1) seeds the floor (pre-roll gap); single segment of 1+6 = 7 chunks.
        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6)), new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(1);
        result.Text.ShouldBe("7");
    }

    [Fact]
    public async Task TranscribeAsync_PausesBetweenPhrases_ConcatenatesInOrder()
    {
        var inner = new FakeStt();

        // Leading Silence(1) seeds the floor (pre-roll gap): seg0 = 1 silent + 6 loud + 3 silent = 10 ;
        // seg1 = 7 loud + 3 silent = 10 ; tail seg2 = 8 loud = 8
        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(3);
        result.Text.ShouldBe("10 10 8");
    }

    [Fact]
    public async Task TranscribeAsync_ManySegments_RespectsMaxInFlightDecodes()
    {
        // Each decode holds a slot for 50 ms so overlaps are observable.
        var inner = new FakeStt(async count =>
        {
            await Task.Delay(50);
            return new TranscriptionResult { Text = count.ToString() };
        });

        // Leading Silence(1) seeds the floor (pre-roll gap): without it the smoothed
        // floor tracker seeds itself at speech level (no leading gap to re-seed it),
        // and a 300 ms mid-stream gap alone is shorter than the 500 ms smoothing
        // window and can't pull it back down — no segment would ever close.
        await New(inner, Config(maxInFlight: 1)).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task TranscribeAsync_ManySegments_PermitsOverlapUpToMaxInFlightDecodes()
    {
        // Complement to the cap test above: with maxInFlight=2 the decoder MUST actually overlap two
        // segment decodes — the latency optimization that justifies this class. A hardcoded-serial
        // implementation (or one ignoring the config) would pass the cap test but fail this one.
        var inner = new FakeStt(async count =>
        {
            await Task.Delay(50);
            return new TranscriptionResult { Text = count.ToString() };
        });

        // Leading Silence(1) seeds the floor (pre-roll gap) — see the identical note
        // on TranscribeAsync_ManySegments_RespectsMaxInFlightDecodes above.
        // ChainContext off: chaining makes a segment await its predecessor's transcript, which
        // serializes decodes by construction — pinned by the test below.
        await New(inner, Config(maxInFlight: 2) with { ChainContext = false }).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.MaxConcurrent.ShouldBe(2);
    }

    [Fact]
    public async Task TranscribeAsync_ChainContext_SerializesDecodesRegardlessOfMaxInFlight()
    {
        var inner = new FakeStt(async count =>
        {
            await Task.Delay(50);
            return new TranscriptionResult { Text = count.ToString() };
        });

        await New(inner, Config(maxInFlight: 2) with { ChainContext = true }).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task TranscribeAsync_ShortFinalPhrase_MergesBackwardIntoPreviousSegment()
    {
        var inner = new FakeStt();

        // seg0 = 1 silent + 6 loud + 3 silent = 10 ; tail = 2 loud (200 ms < 500 ms min) -> merge into seg0 => 12
        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(2)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("12"); // single merged segment, not "10 2"
    }

    [Fact]
    public async Task TranscribeAsync_SegmentDecodeFails_FallsBackToWholeUtterance()
    {
        // seg0 = 1 silent + 6 loud + 3 silent = 10, tail seg1 = 7 chunks, whole = 17. Segment-sized decodes
        // throw; only the whole-utterance fallback (17 chunks) succeeds.
        var inner = new FakeStt(count => count >= 16
            ? Task.FromResult(new TranscriptionResult { Text = "whole" })
            : throw new InvalidOperationException("segment decode boom"));

        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(7)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("whole");
    }

    [Fact]
    public async Task TranscribeAsync_AllSegmentsReportConfidence_AggregatesMean()
    {
        var inner = new FakeStt(count =>
            Task.FromResult(new TranscriptionResult { Text = count.ToString(), Confidence = 0.8 }));

        // Leading Silence(1) seeds the floor (pre-roll gap) — see the identical note
        // on TranscribeAsync_ManySegments_RespectsMaxInFlightDecodes above.
        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(7)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Confidence.ShouldNotBeNull();
        result.Confidence!.Value.ShouldBe(0.8, 1e-9);
    }

    [Fact]
    public async Task TranscribeAsync_NoSegmentReportsConfidence_IsNull()
    {
        var inner = new FakeStt(); // default handler returns Confidence = null

        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(7)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Confidence.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_SegmentsOfDifferentLengths_WeightsConfidenceByDuration()
    {
        // Leading Silence(1) seeds the floor (pre-roll gap): seg0 = 1 silent + 6 loud + 3 silent = 10
        // chunks (0.9); tail seg1 = 12 loud = 12 chunks (0.2).
        // Duration-weighted mean (10*0.9 + 12*0.2)/22 = 0.5181...; an unweighted mean would be 0.55.
        var inner = new FakeStt(count => Task.FromResult(
            new TranscriptionResult { Text = count.ToString(), Confidence = count == 10 ? 0.9 : 0.2 }));

        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(12)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Confidence.ShouldNotBeNull();
        result.Confidence!.Value.ShouldBe((10 * 0.9 + 12 * 0.2) / 22, 1e-9);
    }

    [Fact]
    public async Task TranscribeAsync_AggregatesWhisperStats_WeightedMeansAndMaxCompression()
    {
        // Leading Silence(1) seeds the floor (pre-roll gap): seg0 = 10 chunks, tail seg1 = 12 chunks.
        var inner = new FakeStt(count => Task.FromResult(new TranscriptionResult
        {
            Text = count.ToString(),
            AvgLogProb = count == 10 ? -0.2 : -1.0,
            NoSpeechProb = count == 10 ? 0.1 : 0.7,
            CompressionRatio = count == 10 ? 1.1 : 2.9
        }));

        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(12)),
            new TranscriptionOptions(), CancellationToken.None);

        result.AvgLogProb.ShouldNotBeNull();
        result.AvgLogProb!.Value.ShouldBe((10 * -0.2 + 12 * -1.0) / 22, 1e-9);
        result.NoSpeechProb!.Value.ShouldBe((10 * 0.1 + 12 * 0.7) / 22, 1e-9);
        result.CompressionRatio.ShouldBe(2.9);
    }

    [Fact]
    public async Task TranscribeAsync_MixedConfidenceAvailability_AveragesOnlyReportingSegments()
    {
        // Fail-open composition: a segment without stats must not zero the average.
        // Leading Silence(1) seeds the floor (pre-roll gap): seg0 = 10 chunks.
        var inner = new FakeStt(count => Task.FromResult(new TranscriptionResult
        { Text = count.ToString(), Confidence = count == 10 ? 0.6 : null }));

        var result = await New(inner).TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(3), Speech(12)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Confidence.ShouldBe(0.6);
    }

    [Fact]
    public async Task TranscribeAsync_SpeechPhrasesOverBabble_StillSlicesSegments()
    {
        var inner = new FakeStt();
        // 800 ms floor window — longer than the 600 ms constant-amplitude speech runs, since synthetic
        // speech has no intra-word dips to re-seed the windowed-min floor (real speech does).
        var sut = new SegmentedSpeechToText(
            inner, Config(), new WyomingClientSettings { FloorWindowMs = 800 },
            NullLogger<SegmentedSpeechToText>.Instance);

        // babble(8): floor converges at 2000; speech(6): a 600 ms phrase (> 500 ms
        // MinSegmentMs); babble(5): inter-phrase "silence" — needs >= the 500 ms
        // smoothing window (not just >= 300 ms SegmentSilenceMs) for the smoothed
        // floor to fully return to babble level before the next phrase, else it
        // stays elevated from the preceding speech burst and the second phrase
        // never crosses the entry bar; second phrase; babble tail.
        var result = await sut.TranscribeAsync(
            Stream(Babble(8), Speech(6), Babble(5), Speech(6), Babble(4)),
            new TranscriptionOptions(), CancellationToken.None);

        // With the old fixed 500 threshold, babble RMS 2000 never reads as silence and
        // the whole stream decodes as ONE segment; adaptively it must slice at least twice.
        inner.Calls.ShouldBeGreaterThanOrEqualTo(2);
        result.Text.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TranscribeAsync_UtteranceShorterThanFirstSplit_DecodesAsOneSegment()
    {
        var inner = new FakeStt();
        // Leading Silence(1) seeds the floor (pre-roll gap), as in the tests above; the rest is
        // 2.3 s of audio with two internal pauses, all under a 4 s first-split floor.
        var sut = New(inner, Config(firstSplitAfterMs: 4000));

        await sut.TranscribeAsync(
            Stream(Silence(1), Speech(8), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task TranscribeAsync_UtterancePastFirstSplit_ResumesSplitting()
    {
        var inner = new FakeStt();
        // 1 + 12 chunks put 1.3 s of audio behind the stream before the first pause closes a
        // segment, so the 1 s floor is already crossed and both pauses split.
        var sut = New(inner, Config(firstSplitAfterMs: 1000));

        await sut.TranscribeAsync(
            Stream(Silence(1), Speech(12), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(2);
    }

    // Records the options each segment was decoded with, and returns a distinct word per call so
    // the chained prompt is identifiable.
    private sealed class OptionsRecordingStt : ISpeechToText
    {
        private readonly Lock _lock = new();
        private int _calls;
        public List<string?> PriorTexts { get; } = [];

        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            await foreach (var _ in audio.WithCancellation(ct))
            {
            }

            int index;
            lock (_lock)
            {
                PriorTexts.Add(options.PriorText);
                index = _calls++;
            }
            return new TranscriptionResult { Text = $"seg{index}" };
        }
    }

    [Fact]
    public async Task TranscribeAsync_ChainContext_PassesThePriorSegmentTextAsThePriorText()
    {
        var inner = new OptionsRecordingStt();
        var sut = New(inner, Config() with { ChainContext = true });

        await sut.TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.PriorTexts.Count.ShouldBe(2);
        inner.PriorTexts[0].ShouldBeNull();
        inner.PriorTexts[1].ShouldBe("seg0");
    }

    [Fact]
    public async Task TranscribeAsync_ChainContextOff_LeavesThePriorTextAlone()
    {
        var inner = new OptionsRecordingStt();
        var sut = New(inner, Config() with { ChainContext = false });

        await sut.TranscribeAsync(
            Stream(Silence(1), Speech(6), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.PriorTexts.Count.ShouldBe(2);
        inner.PriorTexts.ShouldAllBe(p => p == null);
    }
}