using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// One place decides how a satellite's endpointing gate is put together. Before this the gate was
// assembled at each call site, which is how two sites came to resolve it differently — the wake
// capture capped its noise floor with the quietest recent reading from the room and the approval
// capture did not.
public class SilenceGateFactoryTests
{
    private static readonly SatelliteConfig _plain = new() { Identity = "household", Room = "Kitchen" };

    private static SilenceGateFactory MakeFactory(WyomingClientSettings? wyoming = null) =>
        new(new VoiceSettings { FollowUp = new FollowUpSettings { WindowMs = 2000 } },
            wyoming ?? new WyomingClientSettings(),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

    // Constant-amplitude S16LE: for a flat signal the RMS is the amplitude itself, so a test can
    // ask for the exact level it wants the gate to classify. 3200 bytes = 100 ms at 16 kHz mono.
    private static AudioChunk Level(short amplitude)
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)(amplitude >> 8);
        }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static void Feed(SilenceGate gate, AudioChunk chunk, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            gate.Process(chunk.Data.Span, chunk.Format.SampleRateHz,
                chunk.Format.SampleWidthBytes, chunk.Format.Channels);
        }
    }

    // Silence first, then the burst: a capture that opens straight onto sound has nothing but that
    // sound to measure its floor from, so a lone loud chunk never clears its own entry bar.
    private static bool HearsSpeechAt(SilenceGate gate, short amplitude)
    {
        Feed(gate, Level(0), times: 5);
        Feed(gate, Level(amplitude));
        return gate.LastChunkWasSpeech;
    }

    [Fact]
    public void Create_PerSatelliteGateOverride_BeatsTheGlobalThreshold()
    {
        var factory = MakeFactory(new WyomingClientSettings { SilenceRmsThreshold = 500 });
        var loudRoom = _plain with { Gate = new GateSettings { SilenceRmsThreshold = 50_000 } };

        HearsSpeechAt(factory.Create("kitchen-01", _plain), 2000).ShouldBeTrue();
        HearsSpeechAt(factory.Create("loud-room-01", loudRoom), 2000).ShouldBeFalse();
    }

    [Fact]
    public void Create_AfterARecordedRoomSample_CapsTheGateFloorAtIt()
    {
        // The threshold is above everything fed below, so nothing latches as speech and the floor
        // keeps measuring — which is the case where an uncapped floor drifts up to the background.
        var factory = MakeFactory(new WyomingClientSettings { SilenceRmsThreshold = 20_000 });

        var uncapped = factory.Create("kitchen-01", _plain);
        Feed(uncapped, Level(800), times: 10);
        uncapped.FloorRms.ShouldBe(800, tolerance: 20);

        factory.RecordRoomLevel("kitchen-01", 100);

        var capped = factory.Create("kitchen-01", _plain);
        Feed(capped, Level(800), times: 10);
        capped.FloorRms.ShouldBe(100, tolerance: 1);
        // The cap only ever lowers the floor: what the gate measured is still the reading worth
        // remembering, so a remembered level is never re-derived from itself.
        capped.MeasuredFloorRms.ShouldBe(800, tolerance: 20);
    }

    [Fact]
    public void RecordRoomLevel_IsKeyedBySatellite()
    {
        var factory = MakeFactory(new WyomingClientSettings { SilenceRmsThreshold = 20_000 });
        factory.RecordRoomLevel("kitchen-01", 100);

        var other = factory.Create("office-01", _plain);
        Feed(other, Level(800), times: 10);

        other.FloorRms.ShouldBe(800, tolerance: 20);
    }

    [Theory]
    // A capture that heard no speech spent its whole window measuring the background.
    [InlineData("no_speech", 90.0, 0.0, 90.0)]
    // One that ended on trailing silence measured it over the run that ended it.
    [InlineData("trailing_silence", 500.0, 70.0, 70.0)]
    public void RecordCaptureClose_MeasuringEndReasons_RecordThatSample(
        string endReason, double measuredFloor, double trailing, double expectedCap)
    {
        var factory = MakeFactory(new WyomingClientSettings { SilenceRmsThreshold = 20_000 });
        factory.RecordCaptureClose("kitchen-01", new CaptureStats(
            PeakRms: 4000, FloorRms: measuredFloor, SpeechMs: 0, EndReason: endReason,
            TrailingRms: trailing, TrailingSilenceMs: 800, MeasuredFloorRms: measuredFloor));

        var gate = factory.Create("kitchen-01", _plain);
        Feed(gate, Level(800), times: 10);

        gate.FloorRms.ShouldBe(expectedCap, tolerance: 1);
    }

    [Theory]
    // Abandoned to arbitration, forced by an audio-stop, or capped at max-utterance: none of these
    // established what silence sounded like, so none of them may cap the next capture.
    [InlineData("forced")]
    [InlineData("max_utterance")]
    [InlineData(null)]
    public void RecordCaptureClose_NonMeasuringEndReasons_RecordNothing(string? endReason)
    {
        var factory = MakeFactory(new WyomingClientSettings { SilenceRmsThreshold = 20_000 });
        factory.RecordCaptureClose("kitchen-01", new CaptureStats(
            PeakRms: 4000, FloorRms: 90, SpeechMs: 1200, EndReason: endReason,
            TrailingRms: 70, TrailingSilenceMs: 800, MeasuredFloorRms: 90));

        var gate = factory.Create("kitchen-01", _plain);
        Feed(gate, Level(800), times: 10);

        gate.FloorRms.ShouldBe(800, tolerance: 20);
    }

    [Fact]
    public void RecordCaptureClose_TrailingSilenceWithNoTrailingRun_RecordsNothing()
    {
        // A capture that ended without ever accumulating a trailing run reports 0 — an absent
        // measurement, not a silent room. Recording it would pin the cap at silence.
        var factory = MakeFactory(new WyomingClientSettings { SilenceRmsThreshold = 20_000 });
        factory.RecordCaptureClose("kitchen-01", new CaptureStats(
            PeakRms: 4000, FloorRms: 90, SpeechMs: 1200, EndReason: "trailing_silence",
            TrailingRms: 70, TrailingSilenceMs: 0, MeasuredFloorRms: 90));

        var gate = factory.Create("kitchen-01", _plain);
        Feed(gate, Level(800), times: 10);

        gate.FloorRms.ShouldBe(800, tolerance: 20);
    }
}