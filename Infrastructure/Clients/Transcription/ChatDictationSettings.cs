namespace Infrastructure.Clients.Transcription;

// Chat dictation gets its own section rather than sharing the voice channel's, whose short-phrase
// prompt and room placeholders are tuned for satellite commands and would skew a long spoken
// paragraph. All of it is generic and lives in appsettings.json alone. Each chat channel derives
// its own DictationSettings from this, adding only what its transport needs.
public record ChatDictationSettings
{
    public TranscriptionClientConfig Transcription { get; init; } = new() { Language = "es" };

    // A pocketed phone must not record indefinitely, and a note too long to be worth transcribing
    // should be refused before a byte is fetched: the browser is handed this so it can stop itself
    // (the endpoint enforces it anyway), and Telegram checks it against the duration the update
    // reports before downloading.
    public TimeSpan MaxLength { get; init; } = TimeSpan.FromMinutes(2);
}