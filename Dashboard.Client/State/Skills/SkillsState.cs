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
    // The last how many days the page reads: a rolling window the server resolves on every read.
    public int Days { get; init; } = 1;
    public Aggregation Agg { get; init; } = Aggregation.Avg;
}