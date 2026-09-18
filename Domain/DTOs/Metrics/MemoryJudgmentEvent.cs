namespace Domain.DTOs.Metrics;

// One judgment asked of Jev about memory, whatever it answered: which of the three, the verdicts,
// and what they decided. A dropped candidate carries its text and scores and a pair judgment its
// links, so every bar in the settings can be re-read from production rather than from the probe.
public record MemoryJudgmentEvent : MetricEvent
{
    // One of MemoryJudgmentKinds: gate, verify, pairs.
    public required string Kind { get; init; }

    public required string UserId { get; init; }

    // False when the judge was slow, down or misconfigured and the call fell back to today's
    // behaviour; the scores are then absent.
    public required bool Answered { get; init; }

    // The noul answers by question id (gate, verify), or the pair choices' confidences (pairs).
    public IReadOnlyDictionary<string, double>? Scores { get; init; }

    // Gate: whether the extractor was skipped.
    public bool? Skipped { get; init; }

    // Verify: the candidate, its category, and whether it was dropped.
    public string? Candidate { get; init; }

    public string? Category { get; init; }

    public bool? Dropped { get; init; }

    // Pairs: the memories judged, by id, the relation chosen per pair keyed "i-j" by index, and
    // the linked groups the merge model was then called with.
    public IReadOnlyList<string>? MemoryIds { get; init; }

    public IReadOnlyDictionary<string, string>? Relations { get; init; }

    public IReadOnlyList<IReadOnlyList<string>>? Linked { get; init; }

    public long? DurationMs { get; init; }

    public int? InputTokens { get; init; }

    public string? Model { get; init; }
}

public static class MemoryJudgmentKinds
{
    public const string Gate = "gate";
    public const string Verify = "verify";
    public const string Pairs = "pairs";
}