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
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}