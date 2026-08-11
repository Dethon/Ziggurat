using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The microphone knows nothing about turns. What it does own is the pairing a listening path used
// to have to remember: closing a capture and telling the room-noise memory what it measured are one
// act, so a path that closes cannot forget to pay.
public class MicrophoneTests
{
    private static readonly SatelliteConfig _config = new() { Identity = "household", Room = "Kitchen" };

    private static SilenceGateFactory Gates() => new(
        // The threshold sits above every level fed below, so nothing latches as speech and the
        // capture spends its whole window measuring the background.
        new VoiceSettings { FollowUp = new FollowUpSettings { WindowMs = 2000 } },
        new WyomingClientSettings { SilenceRmsThreshold = 20_000 },
        new FakeTimeProvider(DateTimeOffset.UtcNow));

    private static void Feed(Microphone mic, short amplitude, int times)
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(amplitude & 0xFF);
            pcm[i + 1] = (byte)(amplitude >> 8);
        }
        var chunk = new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
        Enumerable.Range(0, times).ToList().ForEach(_ => mic.Feed(chunk));
    }

    private static Microphone NewMic(SilenceGateFactory gates) => new("kitchen-01", gates);

    private static UtteranceCapture Open(Microphone mic, SilenceGateFactory gates) =>
        mic.Open(gates.Create("kitchen-01", _config), new ChunkHistory(TimeProvider.System, TimeSpan.FromSeconds(5)));

    [Fact]
    public void IsOpen_AcrossOpenAndClose_ReportsWhetherTheMicrophoneIsListening()
    {
        var gates = Gates();
        var mic = NewMic(gates);
        mic.IsOpen.ShouldBeFalse();

        var capture = Open(mic, gates);
        mic.IsOpen.ShouldBeTrue();

        mic.Close(capture);
        mic.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void Close_ACaptureThatIsNoLongerTheOpenOne_LeavesTheLiveOneAttached()
    {
        // Closing is by identity: a late close from a capture that has already been replaced (an
        // arbitration abort racing the next turn's open) must detach nothing, or the live capture
        // stops receiving audio while the satellite is still streaming it.
        var gates = Gates();
        var mic = NewMic(gates);

        var replaced = Open(mic, gates);
        Open(mic, gates);

        mic.Close(replaced);

        mic.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task Feed_NoCaptureOpen_ReachesNothingAndDoesNotThrow()
    {
        var gates = new SilenceGateFactory(
            new VoiceSettings { FollowUp = new FollowUpSettings { WindowMs = 2000 } },
            new WyomingClientSettings
            {
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 1000,
                MinSpeechMs = 100
            },
            new FakeTimeProvider(DateTimeOffset.UtcNow));
        var mic = NewMic(gates);

        // Nothing open: feeding is a safe no-op.
        Should.NotThrow(() => Feed(mic, 8000, times: 1));

        var capture = Open(mic, gates);
        Feed(mic, 0, times: 1);        // pre-roll gap seeds the floor
        Feed(mic, 8000, times: 2);
        Feed(mic, 0, times: 2);
        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        mic.Close(capture);
        Should.NotThrow(() => Feed(mic, 8000, times: 1));
    }

    [Fact]
    public async Task Close_ANoSpeechCapture_PaysWhatItMeasuredIntoTheRoomNoiseMemory()
    {
        // The memory is only observable through the next gate built on the same satellite: it caps
        // that gate's floor. A capture that closed without paying leaves the next one uncapped.
        var gates = Gates();
        var mic = NewMic(gates);

        var first = Open(mic, gates);
        Feed(mic, 300, times: 25);   // a whole no-speech window of quiet background
        (await first.Completed).ShouldBe(CaptureOutcome.NoSpeech);
        mic.Close(first);

        var second = Open(mic, gates);
        Feed(mic, 900, times: 10);

        second.Stats.FloorRms.ShouldBe(300, tolerance: 20);
    }

    [Fact]
    public async Task TryAbort_ACaptureIsOpen_AbandonsIt()
    {
        var gates = Gates();
        var mic = NewMic(gates);
        var capture = Open(mic, gates);

        mic.TryAbort().ShouldBeTrue();

        (await capture.Completed).ShouldBe(CaptureOutcome.Abandoned);
        mic.TryAbort().ShouldBeFalse();   // nothing left to abort
    }
}