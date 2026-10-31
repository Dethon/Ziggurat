namespace Domain.DTOs.Voice;

public record TranscriptionResult
{
    public required string Text { get; init; }
    public string? Language { get; init; }
    public double? Confidence { get; init; }

    // Raw whisper quality signals, duration-weighted per POST by OpenAiSpeechToText and again
    // across utterance segments by SegmentedSpeechToText. AvgLogProb/NoSpeechProb gate dispatch
    // (TranscriptDispatcher); null means "no signal" and fails open. Confidence and
    // CompressionRatio stay for metrics but Lemonade emits neither (always null).
    public double? AvgLogProb { get; init; }
    public double? NoSpeechProb { get; init; }
    public double? CompressionRatio { get; init; }

    // How long the recording was, as whisper reported it (verbose_json's duration, else where its
    // last segment ended); null when the body said neither.
    public double? AudioSeconds { get; init; }
}