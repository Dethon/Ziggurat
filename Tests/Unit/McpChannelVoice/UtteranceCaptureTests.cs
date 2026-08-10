using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class UtteranceCaptureTests
{
    private const int Bytes = 3200; // 100 ms at 16 kHz/16-bit mono

    private static AudioChunk Loud()
    {
        var pcm = new byte[Bytes];
        for (var i = 0; i < pcm.Length; i += 2)
        { pcm[i] = 0x40; pcm[i + 1] = 0x1F; }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static AudioChunk Silent() =>
        new() { Data = new byte[Bytes], Format = AudioFormat.WyomingStandard };

    private static SilenceGate Gate(int noSpeechMs = 0) => new(
        new AdaptiveLevelTracker(
            clampRms: 500, enterMarginDb: 9, exitMarginDb: 4, peakDropDb: 15,
            floorWindow: TimeSpan.FromSeconds(3)),
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(5000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(noSpeechMs));

    [Fact]
    public async Task Feed_SpeechThenSilence_CompletesEndedAndExposesAudio()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        var count = 0;
        await foreach (var _ in capture.Audio)
        { count++; }
        count.ShouldBe(5);
    }

    [Fact]
    public async Task Feed_OnlySilenceWithinWindow_CompletesNoSpeech()
    {
        var capture = new UtteranceCapture(Gate(noSpeechMs: 300));

        capture.Feed(Silent());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.NoSpeech);
    }

    [Fact]
    public async Task ForceEnd_CompletesEnded()
    {
        var capture = new UtteranceCapture(Gate());
        capture.Feed(Loud());
        capture.ForceEnd();
        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
    }

    [Fact]
    public async Task Stats_AfterEndedCapture_ReportsPeakRmsAndSpeechMs()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.PeakRms.ShouldBe(8000, 1.0);
        capture.Stats.SpeechMs.ShouldBe(200);
    }

    [Fact]
    public async Task Stats_AfterTrailingSilenceEnd_CarriesTrailingRms()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent());
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.TrailingRms.ShouldBe(0, 1.0); // trailing run was true silence
    }

    [Fact]
    public async Task Stats_AfterForceEnd_ReportsForced()
    {
        var capture = new UtteranceCapture(Gate());
        capture.Feed(Silent());
        capture.Feed(Loud());

        capture.ForceEnd();

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.EndReason.ShouldBe("forced");
    }

    [Fact]
    public async Task Stats_ForceEndAfterNaturalEnd_KeepsNaturalEndReason()
    {
        var capture = new UtteranceCapture(Gate());
        capture.Feed(Silent());
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent()); // trailing silence ends the capture naturally

        capture.ForceEnd(); // late audio-stop must not relabel it

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.EndReason.ShouldBe("trailing_silence");
    }

    [Fact]
    public async Task BufferedAudio_ContainsEveryChunkContinuous()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent()); // trailing silence ends the capture

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        // The verifier embeds continuous, enrollment-matching audio: every fed chunk, not the
        // silence-cut speech-only subset (embedding glued fragments collapses CAM++ similarity).
        capture.BufferedAudio.Count.ShouldBe(5);
        capture.BufferedAudio.ShouldAllBe(c => c.Data.Length == 3200);
    }

    [Fact]
    public async Task Stats_AfterTrailingSilenceEnd_CarryTheEndpointingTail()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.EndReason.ShouldBe("trailing_silence");
        capture.Stats.TrailingSilenceMs.ShouldBe(200);
    }

    [Fact]
    public async Task Stats_FedAfterTheCaptureEnded_KeepTheTailTheGateDecidedOn()
    {
        // Feed has no post-completion guard and the satellite keeps streaming until it receives the
        // closing transcript, while the host reads Stats later still (after speaker verification).
        // The tail must stay the gate's decision, or EndpointTailMs reports silence-until-close and
        // the speech-end anchor rewinds too far.
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent());
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());
        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        capture.Feed(Silent());
        capture.Feed(Silent());

        capture.Stats.TrailingSilenceMs.ShouldBe(200);
    }

    private static SilenceGate LenientGate() => new(
        new AdaptiveLevelTracker(500, 9, 4, 15, TimeSpan.FromSeconds(3)),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5000), TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task Abort_OpenCapture_SettlesAbandonedAndReturnsTrue()
    {
        var capture = new UtteranceCapture(LenientGate());

        capture.Abort().ShouldBeTrue();

        capture.Completed.IsCompletedSuccessfully.ShouldBeTrue();
        (await capture.Completed).ShouldBe(CaptureOutcome.Abandoned);
    }

    [Fact]
    public async Task Abort_AlreadyEndedCapture_ReturnsFalseAndKeepsOutcome()
    {
        var capture = new UtteranceCapture(LenientGate());
        capture.ForceEnd();

        capture.Abort().ShouldBeFalse();

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
    }

    [Fact]
    public void Feed_WithHistory_RecordsGateVerdictPerChunk()
    {
        var time = new FakeTimeProvider();
        var history = new ChunkHistory(time, TimeSpan.FromSeconds(5));
        var capture = new UtteranceCapture(LenientGate(), history);

        // AdaptiveLevelTracker seeds its floor from the very first frame it sees (see
        // AdaptiveLevelTrackerTests.IsSpeech_LoudTransientBeforeSpeech_DoesNotPoisonPeakBackstop):
        // a loud opening frame with no pre-roll seeds the floor at its own level and reads as
        // silence. A silent pre-roll first (as every other test in this file does) lets the
        // loud chunk that follows actually cross the entry bar.
        capture.Feed(Chunk(0));    // pre-roll: seeds the floor near silence
        capture.Feed(Chunk(3000)); // loud chunk: now classified speech

        var samples = history.Snapshot();
        samples.Count.ShouldBe(2);
        samples[0].IsSpeech.ShouldBeFalse();
        samples[1].IsSpeech.ShouldBeTrue();
        samples[1].Rms.ShouldBe(3000, tolerance: 1);
    }

    private static AudioChunk Chunk(short amplitude, int samples = 1280)
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