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

    // Candidates the check refused to store. Dropped plus stored plus what the embedding dedup
    // declined is the number of candidates actually considered, which is the extractor's answer
    // capped at MaxCandidatesPerMessage — so on a turn over the cap it is less than CandidateCount,
    // and the remainder is the tail nothing looked at rather than a dedup decision.
    public int DroppedCount { get; init; }
}

// How an extraction ended, so "found nothing" and "broke" stop sharing a zero. A gated turn was
// judged to hold nothing lasting and the extractor was never asked; an empty one was asked and
// answered nothing; a failed one exhausted its retries or broke on the way to the store — its
// counts are whatever the candidates settled before the throw, not zeros.
public static class MemoryExtractionOutcomes
{
    public const string Gated = "gated";
    public const string Empty = "empty";
    public const string Extracted = "extracted";
    public const string Failed = "failed";
}