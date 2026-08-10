using Domain.DTOs.Voice;

namespace Domain.Contracts;

// The single injection point for dictation: complete audio plus the container it is in, answered
// with a transcript and the confidence figures whisper reported. Telegram gates on those figures
// and WebChat does not, because there a person reads the words before sending them.
public interface IAudioTranscriber
{
    Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}