using Concentus;
using Concentus.Oggfile;
using Domain.DTOs.Voice;

namespace McpChannelTelegram.Services;

// Telegram voice notes are Ogg/Opus, and whisper-server as Lemonade runs it decodes through
// miniaudio plus stb_vorbis: WAV, MP3, FLAC and Ogg/Vorbis are accepted, Opus in any container is
// a 400. The image carries no ffmpeg and whisper.cpp's ffmpeg fallback exists only on its
// file-path branch, so the decode happens here, in managed code, with no native dependency and no
// change to any container image.
//
// Opus always runs at 48 kHz internally, so the decoder is asked for 48 kHz and the speexdsp
// resampler bundled with Concentus takes it down to the 16 kHz whisper wants. Decoding straight at
// 16 kHz is not the shortcut it looks like — a mono stream decoded that way comes back silent.
internal static class OpusVoiceNote
{
    private const int OpusRateHz = 48_000;
    private const int SampleRateHz = 16_000;
    private const int Channels = 1;
    private const int SampleWidthBytes = 2;

    // The shape DecodeToPcm answers in, as the one value a caller wrapping it in a RIFF header
    // needs.
    public static AudioFormat Format { get; } = new()
    {
        SampleRateHz = SampleRateHz,
        Channels = Channels,
        SampleWidthBytes = SampleWidthBytes
    };

    // Concentus 2 probes for a native libopus and falls back to its managed implementation. The
    // channel image has no such library, and a decode that silently depends on one being installed
    // is a deployment surprise waiting to happen.
    static OpusVoiceNote() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

    // Answers s16le PCM at 16 kHz mono. A container that is not Ogg/Opus — or is truncated past
    // repair — throws, which the channel turns into the could-not-understand reply.
    public static byte[] DecodeToPcm(ReadOnlyMemory<byte> ogg)
    {
        // A stereo note is downmixed by the decoder itself, so one mono decoder covers both.
        var decoder = OpusCodecFactory.CreateDecoder(OpusRateHz, Channels);
        using var source = new MemoryStream(ogg.ToArray(), writable: false);
        var reader = new OpusOggReadStream(decoder, source);

        var decoded = new List<short>();
        while (reader.HasNextPacket)
        {
            if (reader.DecodeNextPacket() is { } packet)
            {
                decoded.AddRange(packet);
            }
        }

        if (decoded.Count == 0)
        {
            throw new InvalidDataException(
                $"Could not read the recording as Ogg/Opus: {reader.LastError ?? "no audio in it"}");
        }

        return ToBytes(Downsample([.. decoded]));
    }

    private static short[] Downsample(short[] pcm48k)
    {
        // Quality 5 of 10: the speexdsp mid-point, which is what every other 48-to-16 downmix in
        // this system runs at, and far above what a 16 kHz whisper input can show.
        var resampler = ResamplerFactory.CreateResampler(Channels, OpusRateHz, SampleRateHz, 5);
        // One extra millisecond of headroom: the resampler emits ceil(in / ratio) frames and the
        // filter's own latency lands inside that, so a tight buffer would truncate the tail.
        var resampled = new short[pcm48k.Length * SampleRateHz / OpusRateHz + SampleRateHz / 1000];
        var read = pcm48k.Length;
        var written = resampled.Length;
        resampler.ProcessInterleaved(pcm48k.AsSpan(), ref read, resampled.AsSpan(), ref written);
        return resampled[..written];
    }

    private static byte[] ToBytes(short[] pcm)
    {
        var bytes = new byte[pcm.Length * SampleWidthBytes];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}