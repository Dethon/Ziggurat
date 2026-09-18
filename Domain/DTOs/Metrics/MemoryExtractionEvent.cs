namespace Domain.DTOs.Metrics;

public record MemoryExtractionEvent : MetricEvent
{
    public required long DurationMs { get; init; }
    public required int CandidateCount { get; init; }
    public required int StoredCount { get; init; }
    public required string UserId { get; init; }

    // One of MemoryExtractionOutcomes. Not required: events stored before it existed still read,
    // and the page shows them as unrecorded rather than guessing.
    public string? Outcome { get; init; }

    // Candidates the check refused to store. Candidates minus dropped minus stored is what the
    // embedding dedup declined, which is how it always was.
    public int DroppedCount { get; init; }
}

// How an extraction ended, so "found nothing" and "broke" stop sharing a zero. A gated turn was
// judged to hold nothing lasting and the extractor was never asked; an empty one was asked and
// answered nothing; a failed one exhausted its retries.
public static class MemoryExtractionOutcomes
{
    public const string Gated = "gated";
    public const string Empty = "empty";
    public const string Extracted = "extracted";
    public const string Failed = "failed";
}