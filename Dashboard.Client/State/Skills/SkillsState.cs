using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Skills;

public record SkillsState
{
    public IReadOnlyList<SkillPreloadEvent> Events { get; init; } = [];
    public IReadOnlyList<LatencyTrendSeries> Trend { get; init; } = [];
    public SkillPreloadDimension GroupBy { get; init; } = SkillPreloadDimension.Outcome;
    public SkillPreloadMetric Metric { get; init; } = SkillPreloadMetric.Count;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public Aggregation Agg { get; init; } = Aggregation.Avg;
}