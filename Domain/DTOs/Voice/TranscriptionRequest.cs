namespace Domain.DTOs.Voice;

// One complete recording handed over in a single piece, which is how both chat channels have it:
// a browser dictation arrives as an uploaded file and a Telegram voice note as a downloaded one.
// The satellites keep the streaming ISpeechToText contract, whose segmenter and target-speaker
// decorator are built around a chunk stream and gain nothing from a bytes-in shape.
public record TranscriptionRequest
{
    public required ReadOnlyMemory<byte> Audio { get; init; }

    // The container the bytes are actually in, decided by the caller from the leading bytes rather
    // than from anything a sender claimed.
    public required string MediaType { get; init; }

    // Only the extension matters to whisper-server; left null the client names the part after the
    // media type.
    public string? FileName { get; init; }

    public string? Language { get; init; }
    public string? Model { get; init; }

    // Already resolved: whatever composes placeholders or prior text does so before this point.
    public string? Prompt { get; init; }

    public TimeSpan? Timeout { get; init; }
}