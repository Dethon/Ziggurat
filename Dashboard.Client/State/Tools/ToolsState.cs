using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tools;

public record ToolsState
{
    public IReadOnlyList<ToolCallEvent> Events { get; init; } = [];
    public ToolDimension GroupBy { get; init; } = ToolDimension.ToolName;
    public ToolMetric Metric { get; init; } = ToolMetric.CallCount;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    // The last how many days the page reads: a rolling window the server resolves on every read.
    public int Days { get; init; } = 1;
}