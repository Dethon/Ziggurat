using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Schedules;

public record SchedulesState
{
    public IReadOnlyList<ScheduleExecutionEvent> Events { get; init; } = [];
    public ScheduleDimension GroupBy { get; init; } = ScheduleDimension.Schedule;
    public Dictionary<string, int> Breakdown { get; init; } = [];
    // The last how many days the page reads: a rolling window the server resolves on every read.
    public int Days { get; init; } = 1;
}