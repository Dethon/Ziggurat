using McpChannelTelegram.Services;
using Shouldly;

namespace Tests.Unit.McpChannelTelegram;

// whisper as Lemonade runs it rejects Opus in any container — verified against the live instance —
// and the channel image carries no ffmpeg, so a Telegram voice note has to be decoded here before
// it can be transcribed. The fixture is a real Ogg/Opus file (libopus, 48 kHz mono, VoIP), which is
// what Telegram sends.
public class OpusVoiceNoteDecodingTests
{
    private static readonly byte[] Fixture =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Unit/McpChannelTelegram/Fixtures/voice-note.ogg"));

    [Fact]
    public void ARealVoiceNote_DecodesToSixteenKilohertzMonoPcm()
    {
        var pcm = OpusVoiceNote.DecodeToPcm(Fixture);

        // The fixture is two seconds; Opus pads its last frame, so the tail is a few ms long.
        var seconds = pcm.Length / 2.0 / 16_000;
        seconds.ShouldBeInRange(2.0, 2.06);
    }

    // Sample count alone would pass on audio resampled at the wrong ratio, which is exactly the way
    // a 48 kHz decode fed to a 16 kHz header fails: the words come out at a third of their pitch.
    // Counting the fixture's own 440 Hz tone pins the rate to what the samples actually mean.
    [Fact]
    public void TheDecodedToneKeepsItsPitch_SoNothingHasDrifted()
    {
        var pcm = OpusVoiceNote.DecodeToPcm(Fixture);

        var samples = Enumerable.Range(0, pcm.Length / 2)
            .Select(i => BitConverter.ToInt16(pcm, i * 2))
            .ToList();
        var crossings = samples.Zip(samples.Skip(1), (a, b) => a < 0 && b >= 0).Count(x => x);
        var hz = crossings / (samples.Count / 16_000.0);

        hz.ShouldBeInRange(430, 450);
    }

    [Fact]
    public void BytesThatAreNotOggOpus_AreRefusedRatherThanDecodedToSilence()
    {
        Should.Throw<InvalidDataException>(() => OpusVoiceNote.DecodeToPcm(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }
}