using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Memory;

public record MemoryState
{
    public IReadOnlyList<MemoryRecallEvent> RecallEvents { get; init; } = [];
    public IReadOnlyList<MemoryExtractionEvent> ExtractionEvents { get; init; } = [];
    public IReadOnlyList<MemoryDreamingEvent> DreamingEvents { get; init; } = [];
    public IReadOnlyList<MemoryJudgmentEvent> JudgmentEvents { get; init; } = [];
    public MemoryDimension GroupBy { get; init; } = MemoryDimension.User;
    public MemoryMetric Metric { get; init; } = MemoryMetric.Count;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    // The last how many days the page reads: a rolling window the server resolves on every read.
    public int Days { get; init; } = 1;
}