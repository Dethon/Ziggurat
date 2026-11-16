using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services.Tts;

// Counts what the shared Lemonade was asked to say, in characters, once it has started answering:
// a synthesis that failed before any audio cost it nothing worth counting, and one cut short by a
// barge-in was made all the same. The household is not this server's to name — it runs in a
// household's kit, and the relay stamps its tunnel's household on every event it carries.
public sealed class MeteredTextToSpeech(ITextToSpeech inner, IMetricsPublisher metrics) : ITextToSpeech
{
    internal ITextToSpeech Inner => inner;

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text, SynthesisOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var counted = false;
        await foreach (var chunk in inner.SynthesizeAsync(text, options, ct))
        {
            if (!counted)
            {
                counted = true;
                metrics.Publish(new LemonadeUsageEvent
                {
                    Use = LemonadeUse.Speech,
                    Amount = text.Length,
                    Channel = VoiceChannels.Satellite
                });
            }

            yield return chunk;
        }
    }
}