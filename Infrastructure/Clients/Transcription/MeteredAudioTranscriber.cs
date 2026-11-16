using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Transcription;

// Counts each recording the shared Lemonade transcribed, in seconds of speech, against the
// household the request names (a kit server's is the relay's to stamp). Registered around the one
// client every transcribing host holds, so the satellites, Telegram and the chat client are counted
// alike. A WAV recording is measured off its own header; anything posted as it arrived is measured
// by what whisper said it heard, and one neither can measure is not counted rather than counted
// wrong.
public sealed class MeteredAudioTranscriber(IAudioTranscriber inner, IMetricsPublisher metrics) : IAudioTranscriber
{
    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var result = await inner.TranscribeAsync(request, ct);
        if (request.Audio.Length > 0 && (WavAudio.Seconds(request.Audio.Span) ?? result.AudioSeconds) is { } seconds)
        {
            metrics.Publish(new LemonadeUsageEvent
            {
                Use = LemonadeUse.Transcription,
                Amount = seconds,
                TenantId = request.TenantId
            });
        }

        return result;
    }
}