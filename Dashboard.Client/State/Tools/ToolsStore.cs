using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tools;

public record SetToolEvents(IReadOnlyList<ToolCallEvent> Events) : IAction;
public record SetToolBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetToolGroupBy(ToolDimension GroupBy) : IAction;
public record SetToolMetric(ToolMetric Metric) : IAction;
public record AppendToolEvent(ToolCallEvent Event) : IAction;
public record SetToolDays(int Days) : IAction;

public sealed class ToolsStore : Store<ToolsState>
{
    public ToolsStore() : base(new ToolsState()) { }

    public void SetEvents(IReadOnlyList<ToolCallEvent> events) =>
        Dispatch(new SetToolEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetToolBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ToolDimension groupBy) =>
        Dispatch(new SetToolGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(ToolMetric metric) =>
        Dispatch(new SetToolMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendEvent(ToolCallEvent evt) =>
        Dispatch(new AppendToolEvent(evt), static (s, a) => s with
        {
            Events = EventWindow.Append(s.Events, a.Event),
        });

    public void SetDays(int days) =>
        Dispatch(new SetToolDays(days), static (s, a) => s with { Days = a.Days });
}