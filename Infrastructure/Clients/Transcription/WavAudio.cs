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

    // How long a WAV recording is, read off its chunks rather than assuming this class's 44-byte
    // header, because a browser's encoder may write others; null for anything that is not one.
    public static double? Seconds(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            return null;
        }

        int? byteRate = null;
        for (var offset = 12; offset + 8 <= wav.Length;)
        {
            var id = wav.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 4)..]);
            if (id.SequenceEqual("fmt "u8) && offset + 16 <= wav.Length - 4)
            {
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 16)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                var data = Math.Min(size, wav.Length - offset - 8);
                return byteRate is > 0 ? (double)data / byteRate.Value : null;
            }

            if (size < 0)
            {
                return null;
            }

            // Chunks are padded to an even length.
            offset += 8 + size + (size & 1);
        }

        return null;
    }
}