using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SilenceTrimmingTextToSpeechTests
{
    private static AudioFormat Fmt => new() { SampleRateHz = 22050, SampleWidthBytes = 2, Channels = 1 };

    private static AudioChunk Chunk(short value, int samples)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < bytes.Length; i += 2)
        {
            bytes[i] = (byte)(value & 0xFF);
            bytes[i + 1] = (byte)((value >> 8) & 0xFF);
        }
        return new AudioChunk { Data = bytes, Format = Fmt };
    }

    private static AudioChunk Loud(int samples = 100) => Chunk(8000, samples);
    private static AudioChunk Silent(int samples = 100) => Chunk(0, samples);

    private sealed class StubTts(params AudioChunk[] chunks) : ITextToSpeech
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
            string text, SynthesisOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var c in chunks)
            {
                await Task.Yield();
                yield return c;
            }
        }
    }

    private static async Task<byte[]> CollectAsync(ITextToSpeech tts)
    {
        var bytes = new List<byte>();
        await foreach (var c in tts.SynthesizeAsync("hi", new SynthesisOptions(), CancellationToken.None))
        {
            bytes.AddRange(c.Data.ToArray());
        }
        return [.. bytes];
    }

    private static byte[] Concat(params AudioChunk[] chunks) =>
        [.. chunks.SelectMany(c => c.Data.ToArray())];

    [Fact]
    public async Task Synthesize_TrailingSilentChunks_AreDropped()
    {
        var sut = new SilenceTrimmingTextToSpeech(
            new StubTts(Loud(), Loud(), Silent(), Silent()), threshold: 500);

        var output = await CollectAsync(sut);

        output.ShouldBe(Concat(Loud(), Loud()));
    }

    [Fact]
    public async Task Synthesize_InterWordSilence_IsPreserved()
    {
        var sut = new SilenceTrimmingTextToSpeech(
            new StubTts(Loud(), Silent(), Loud(), Silent()), threshold: 500);

        var output = await CollectAsync(sut);

        output.ShouldBe(Concat(Loud(), Silent(), Loud()));
    }

    [Fact]
    public async Task Synthesize_PartialTrailingSilenceWithinChunk_IsTrimmed()
    {
        // One chunk: 100 loud samples followed by 100 silent samples in the same buffer.
        var mixed = new AudioChunk { Data = Concat(Loud(100), Silent(100)), Format = Fmt };
        var sut = new SilenceTrimmingTextToSpeech(new StubTts(mixed), threshold: 500);

        var output = await CollectAsync(sut);

        output.ShouldBe(Loud(100).Data.ToArray());
    }

    [Fact]
    public async Task Synthesize_LeadingSilence_IsPreserved()
    {
        var sut = new SilenceTrimmingTextToSpeech(
            new StubTts(Silent(), Loud()), threshold: 500);

        var output = await CollectAsync(sut);

        output.ShouldBe(Concat(Silent(), Loud()));
    }
}