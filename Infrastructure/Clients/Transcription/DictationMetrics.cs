using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Transcription;

// One dictation, measured the way the satellites already measure an utterance: the same VoiceMetric
// members from both chat channels, so an operator watching whisper get slow after a model change
// sees all three on one dashboard and no new metric family exists. The channel is what tells them
// apart, and Outcome is what tells a transcript that became a turn from one a gate threw away.
//
// Publishing is fire-and-forget by contract (docs/adr/0002), so nothing here is awaited and nothing
// a dictation does depends on it.
public static class DictationMetrics
{
    public static void RecordTranscribed(
        this IMetricsPublisher metrics,
        string channel,
        string outcome,
        TranscriptionResult result,
        long elapsedMs)
    {
        metrics.Publish(new VoiceEvent
        {
            Metric = VoiceMetric.SttLatencyMs,
            Channel = channel,
            DurationMs = elapsedMs
        });
        metrics.Publish(new VoiceEvent
        {
            Metric = VoiceMetric.UtteranceTranscribed,
            Channel = channel,
            Outcome = outcome,
            AvgLogProb = result.AvgLogProb,
            NoSpeechProb = result.NoSpeechProb,
            DurationMs = elapsedMs
        });
    }

    public static void RecordFailure(this IMetricsPublisher metrics, string channel, Exception error) =>
        metrics.Publish(new VoiceEvent
        {
            Metric = VoiceMetric.SttError,
            Channel = channel,
            Error = error.Message
        });
}