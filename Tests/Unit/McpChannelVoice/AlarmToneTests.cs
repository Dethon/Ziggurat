using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AlarmToneTests
{
    [Fact]
    public void Pcm_AlarmAndTimer_ProduceDistinctPatterns()
    {
        AlarmTone.Pcm(AnnounceKind.Alarm).ShouldNotBe(AlarmTone.Pcm(AnnounceKind.Timer));
    }

    [Fact]
    public void Chunk_Uses22050MonoS16le()
    {
        var chunk = AlarmTone.Chunk(AnnounceKind.Timer);

        chunk.Format.SampleRateHz.ShouldBe(22_050);
        chunk.Format.SampleWidthBytes.ShouldBe(2);
        chunk.Format.Channels.ShouldBe(1);
    }

    // The earcon is the attention-grabbing part of an alert, so it must sit near the level of the
    // speech that follows rather than 6 dB below it. Headroom is deliberate: a little is left for
    // the PipeWire mixer, which may be carrying ducked music underneath.
    [Theory]
    [InlineData(AnnounceKind.Alarm)]
    [InlineData(AnnounceKind.Timer)]
    public void Pcm_PeaksNearFullScale(AnnounceKind kind)
    {
        var pcm = AlarmTone.Pcm(kind);

        var peak = Enumerable.Range(0, pcm.Length / 2)
            .Select(i => Math.Abs((int)(short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8))))
            .Max();
        peak.ShouldBeGreaterThan((int)(short.MaxValue * 0.85));
        peak.ShouldBeLessThan(short.MaxValue);
    }
}