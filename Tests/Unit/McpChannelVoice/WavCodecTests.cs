using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tse;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class WavCodecTests
{
    private static AudioChunk Chunk(params byte[] data) =>
        new() { Data = data, Format = AudioFormat.WyomingStandard };

    [Fact]
    public void EncodeWritesCanonical16kMonoHeader()
    {
        var wav = WavCodec.Encode([Chunk(1, 2, 3, 4)]);
        wav.Length.ShouldBe(44 + 4);
        System.Text.Encoding.ASCII.GetString(wav, 0, 4).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).ShouldBe("WAVE");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // channels
        BitConverter.ToInt32(wav, 24).ShouldBe(16000);          // sample rate
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);      // bits/sample
        BitConverter.ToInt32(wav, 40).ShouldBe(4);              // data length
    }

    [Fact]
    public void RoundTripPreservesPayloadAcrossChunks()
    {
        var wav = WavCodec.Encode([Chunk(10, 11), Chunk(12, 13, 14)]);
        var decoded = WavCodec.Decode(wav);
        decoded.Data.ToArray().ShouldBe(new byte[] { 10, 11, 12, 13, 14 });
        decoded.Format.ShouldBe(AudioFormat.WyomingStandard);
    }

    [Fact]
    public void DecodeSkipsForeignSubChunksBeforeData()
    {
        var wav = WavCodec.Encode([Chunk(9, 9)]).ToList();
        // Splice a 4-byte "LIST" sub-chunk between "fmt " and "data" (offset 36).
        wav.InsertRange(36, "LIST"u8.ToArray().Concat(BitConverter.GetBytes(4)).Concat(new byte[] { 0, 0, 0, 0 }));
        var patched = wav.ToArray();
        BitConverter.GetBytes(patched.Length - 8).CopyTo(patched, 4); // fix RIFF size
        WavCodec.Decode(patched).Data.ToArray().ShouldBe(new byte[] { 9, 9 });
    }

    [Fact]
    public void DecodeRejectsNonRiff()
    {
        Should.Throw<InvalidDataException>(() => WavCodec.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
    }

    [Fact]
    public async Task DecodeRejectsNegativeSubChunkSizeInsteadOfHanging()
    {
        var wav = WavCodec.Encode([Chunk(9, 9)]).ToList();
        // Splice a bogus sub-chunk declaring size == -8 between "fmt " and "data" (offset 36).
        // (size & 1) == 0 for -8, so the buggy advance `8 + size + (size & 1)` is exactly zero.
        wav.InsertRange(36, "JUNK"u8.ToArray().Concat(BitConverter.GetBytes(-8)));
        var patched = wav.ToArray();
        BitConverter.GetBytes(patched.Length - 8).CopyTo(patched, 4); // fix RIFF size

        var task = Task.Run(() => WavCodec.Decode(patched));
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        finished.ShouldBe(task, "Decode must not hang forever on a corrupted sub-chunk size");
        await Should.ThrowAsync<InvalidDataException>(task);
    }

    [Theory]
    [InlineData(20, 3)]      // audio format tag: 3 = IEEE float, not PCM
    [InlineData(22, 2)]      // channels: stereo
    [InlineData(34, 8)]      // bits/sample: S8LE
    public void DecodeRejectsForeignFmtChunk(int offset, short value)
    {
        var wav = WavCodec.Encode([Chunk(9, 9)]);
        BitConverter.GetBytes(value).CopyTo(wav, offset);
        Should.Throw<InvalidDataException>(() => WavCodec.Decode(wav));
    }

    [Fact]
    public void DecodeRejectsForeignSampleRate()
    {
        var wav = WavCodec.Encode([Chunk(9, 9)]);
        BitConverter.GetBytes(22050).CopyTo(wav, 24);
        Should.Throw<InvalidDataException>(() => WavCodec.Decode(wav));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_000_000)]
    public void DecodeRejectsSubChunkSizeOverrunningPayload(int declaredSize)
    {
        // A size that overflows `offset + 8 + size` must still fail as InvalidDataException,
        // both when it labels the data chunk and when it labels a chunk the walk skips over.
        var wav = WavCodec.Encode([Chunk(9, 9)]);
        BitConverter.GetBytes(declaredSize).CopyTo(wav, 40); // "data" size
        Should.Throw<InvalidDataException>(() => WavCodec.Decode(wav));

        var spliced = WavCodec.Encode([Chunk(9, 9)]).ToList();
        spliced.InsertRange(36, "JUNK"u8.ToArray().Concat(BitConverter.GetBytes(declaredSize)));
        Should.Throw<InvalidDataException>(() => WavCodec.Decode(spliced.ToArray()));
    }

    [Fact]
    public void DecodeRejectsDataBeforeFmt()
    {
        // "data" ahead of "fmt " would otherwise return audio stamped WyomingStandard
        // without the format ever being checked.
        var wav = WavCodec.Encode([Chunk(9, 9)]).ToList();
        var fmt = wav.GetRange(12, 24);
        wav.RemoveRange(12, 24);
        wav.AddRange(fmt);
        Should.Throw<InvalidDataException>(() => WavCodec.Decode(wav.ToArray()));
    }
}