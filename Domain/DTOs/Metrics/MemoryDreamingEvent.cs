namespace Domain.DTOs.Metrics;

public record MemoryDreamingEvent : MetricEvent
{
    public required int MergedCount { get; init; }
    public required int DecayedCount { get; init; }
    public required bool ProfileRegenerated { get; init; }
    // Not required: events published before this field existed still deserialize.
    public bool ProfileRemoved { get; init; }
    public required string UserId { get; init; }

    // Merges the pass refused to apply, each by the source ids the merge model named: sources
    // that were not linked by the pair judgment, or a merge that came back with no text.
    public IReadOnlyList<IReadOnlyList<string>> RefusedMerges { get; init; } = [];

    public int RefusedCount => RefusedMerges.Count;
}