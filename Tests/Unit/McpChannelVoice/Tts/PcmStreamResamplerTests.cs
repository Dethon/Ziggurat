using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class PcmStreamResamplerTests
{
    private static byte[] Sine24k(int samples, double freqHz, double amplitude)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * freqHz * i / 24000.0));
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    private static short[] ToSamples(byte[] pcm) =>
        Enumerable.Range(0, pcm.Length / 2)
            .Select(i => (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)))
            .ToArray();

    [Fact]
    public void Process_WholeBuffer_ProducesRationalRatioLength()
    {
        var input = Sine24k(1600, 440, 8000);

        var output = new PcmStreamResampler(24000, 22050).Process(input);

        // 1600 samples * 147/160 = 1470
        (output.Length / 2).ShouldBeInRange(1468, 1472);
        (output.Length % 2).ShouldBe(0);
    }

    [Fact]
    public void Process_ChunkedAtArbitraryBoundaries_MatchesWholeBufferExactly()
    {
        var input = Sine24k(4800, 440, 8000);
        var whole = new PcmStreamResampler(24000, 22050).Process(input);

        var chunked = new PcmStreamResampler(24000, 22050);
        var collected = new List<byte>();
        var offsets = new[] { 0, 2, 36, 1038, 1040, 2400, 4802, 9600 }; // even byte offsets
        for (var i = 0; i < offsets.Length - 1; i++)
        {
            collected.AddRange(chunked.Process(input.AsSpan(offsets[i]..offsets[i + 1])));
        }
        collected.AddRange(chunked.Process(input.AsSpan(offsets[^1]..)));

        collected.ToArray().ShouldBe(whole);
    }

    [Fact]
    public void Process_ResampledSine_TracksIdealWaveform()
    {
        // Output sample k sits at time k/22050 s, so it must match the ideal sine there;
        // linear interpolation error for 440 Hz @ 24 kHz is only a few LSBs.
        var input = Sine24k(4800, 440, 8000);

        var samples = ToSamples(new PcmStreamResampler(24000, 22050).Process(input));

        var maxError = samples
            .Select((s, i) => Math.Abs(s - 8000 * Math.Sin(2 * Math.PI * 440 * i / 22050.0)))
            .Max();
        maxError.ShouldBeLessThan(240.0);
    }

    [Fact]
    public void Process_OddByteCount_Throws()
    {
        var resampler = new PcmStreamResampler(24000, 22050);

        Should.Throw<ArgumentException>(() => resampler.Process(new byte[3]));
    }
}