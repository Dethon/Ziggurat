using System.Buffers.Binary;
using System.Text;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Transcription;

// Wraps raw PCM in the RIFF header whisper-server needs. Every dictation path ends here: the
// satellites' chunks, and a Telegram voice note once its Opus has been decoded. whisper as Lemonade
// runs it rejects Opus in any container, so nothing may reach it un-decoded.
public static class WavAudio
{
    public const string MediaType = "audio/wav";

    public static byte[] FromPcm(ReadOnlySpan<byte> pcm, AudioFormat format)
    {
        var wav = new byte[44 + pcm.Length];
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVE", span[8..]);
        Encoding.ASCII.GetBytes("fmt ", span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], format.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(
            span[28..], format.SampleRateHz * format.SampleWidthBytes * format.Channels);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(format.SampleWidthBytes * format.Channels));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)(format.SampleWidthBytes * 8));
        Encoding.ASCII.GetBytes("data", span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[44..]);
        return wav;
    }
}