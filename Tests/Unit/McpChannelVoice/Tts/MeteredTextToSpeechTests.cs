using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

// Every spoken reply, announcement and approval question is made by the shared Lemonade, so each is
// counted in characters. The household is left to the relay, which stamps the one its voice
// server's tunnel was opened for.
public class MeteredTextToSpeechTests
{
    private static readonly SynthesisOptions _options = new();

    [Fact]
    public async Task ASpokenReply_CountsItsCharactersOnceLemonadeAnswers()
    {
        var metrics = new RecordingMetricsPublisher();
        var sut = new MeteredTextToSpeech(new Speaking(chunks: 3), metrics);

        await foreach (var _ in sut.SynthesizeAsync("Son las tres.", _options, CancellationToken.None))
        {
        }

        metrics.Published.OfType<LemonadeUsageEvent>().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Use.ShouldBe(LemonadeUse.Speech),
            e => e.Amount.ShouldBe("Son las tres.".Length),
            e => e.Channel.ShouldBe(VoiceChannels.Satellite),
            e => e.TenantId.ShouldBeNull());
    }

    [Fact]
    public async Task ASynthesisThatFailsBeforeAnyAudio_CountsNothing()
    {
        var metrics = new RecordingMetricsPublisher();
        var sut = new MeteredTextToSpeech(new Speaking(chunks: 0, fails: true), metrics);

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("Hola.", _options, CancellationToken.None))
            {
            }
        });

        metrics.Published.ShouldBeEmpty();
    }

    private sealed class Speaking(int chunks, bool fails = false) : ITextToSpeech
    {
        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
            string text, SynthesisOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (fails)
            {
                throw new HttpRequestException("Lemonade is down");
            }

            for (var i = 0; i < chunks; i++)
            {
                yield return new AudioChunk { Data = new byte[2], Format = AudioFormat.WyomingStandard };
            }
        }
    }
}