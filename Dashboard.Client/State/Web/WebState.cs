using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Web;

public record WebState
{
    public IReadOnlyList<ModalDismissalEvent> Events { get; init; } = [];
    public IReadOnlyList<LatencyTrendSeries> Trend { get; init; } = [];
    public ModalDismissalDimension GroupBy { get; init; } = ModalDismissalDimension.Kind;
    public ModalDismissalMetric Metric { get; init; } = ModalDismissalMetric.Count;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public Aggregation Agg { get; init; } = Aggregation.Avg;
}