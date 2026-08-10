using Infrastructure.Clients.Transcription;

namespace McpChannelSignalR.Settings;

// Chat dictation gets its own section rather than sharing the voice channel's, whose short-phrase
// prompt and room placeholders are tuned for satellite commands and would skew a long spoken
// paragraph. All of it is generic and lives in appsettings.json alone.
public record DictationSettings
{
    public TranscriptionClientConfig Transcription { get; init; } = new() { Language = "es" };

    // A pocketed phone must not record indefinitely. The browser is handed this so it can stop
    // itself, and the endpoint enforces it anyway rather than trusting what arrives.
    public TimeSpan MaxLength { get; init; } = TimeSpan.FromMinutes(2);

    // Below this a press is a mis-tap: nothing is recorded and nothing is sent.
    public TimeSpan MinLength { get; init; } = TimeSpan.FromMilliseconds(400);

    // The cap in bytes, at the one format the browser sends — 16 kHz mono s16le WAV — plus its
    // header and a tenth for headroom. A duration cannot be enforced on a blob without decoding it,
    // and the point of the check is to refuse the oversized post before anything decodes anything.
    // The headroom is not slack: the browser stops itself on a frame boundary rather than a sample
    // one, so a dictation that ran exactly to the cap is a little over the arithmetic, and a
    // ceiling without it would refuse the longest legal recording there is.
    public long MaxBytes => 44 + (long)(MaxLength.TotalSeconds * 16_000 * 2 * 1.1);
}