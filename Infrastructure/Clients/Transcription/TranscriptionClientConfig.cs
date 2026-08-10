namespace Infrastructure.Clients.Transcription;

// What the shared transcription client needs to reach Lemonade. Every server that dictates binds
// its own section onto this record, so a whisper quirk is configured the same way everywhere.
public record TranscriptionClientConfig
{
    public string BaseUrl { get; init; } = "http://lemonade:13305/v1";

    // Lemonade catalog name. The instance pulls models lazily and also holds Large-v3-Turbo, so
    // this is worth a setting rather than a constant.
    public string Model { get; init; } = "Whisper-Medium";

    public string? Language { get; init; }

    // Initial prompt posted with every transcription: it biases spelling and vocabulary. Callers
    // that compose one per request (the voice hub does, from the room and the prior segment) pass
    // it on the request instead.
    public string? Prompt { get; init; }

    // Bounds the POST only. The shared Lemonade HttpClient has an infinite timeout for streaming
    // TTS, so without this a Lemonade that accepts connections but never answers stalls forever.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
}