using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Latency;

public record LatencyState
{
    public IReadOnlyList<LatencyEvent> Events { get; init; } = [];
    public LatencyDimension GroupBy { get; init; } = LatencyDimension.Stage;
    public Aggregation Metric { get; init; } = Aggregation.P95;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public IReadOnlyList<LatencyTrendSeries> Trend { get; init; } = [];
    // The last how many days the page reads: a rolling window the server resolves on every read.
    public int Days { get; init; } = 1;
}