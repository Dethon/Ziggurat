using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Schedules;

public record SetScheduleEvents(IReadOnlyList<ScheduleExecutionEvent> Events) : IAction;
public record SetScheduleBreakdown(Dictionary<string, int> Breakdown) : IAction;
public record SetScheduleGroupBy(ScheduleDimension GroupBy) : IAction;
public record AppendScheduleEvent(ScheduleExecutionEvent Event) : IAction;
public record SetScheduleDays(int Days) : IAction;

public sealed class SchedulesStore : Store<SchedulesState>
{
    public SchedulesStore() : base(new SchedulesState()) { }

    public void SetEvents(IReadOnlyList<ScheduleExecutionEvent> events) =>
        Dispatch(new SetScheduleEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, int> breakdown) =>
        Dispatch(new SetScheduleBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ScheduleDimension groupBy) =>
        Dispatch(new SetScheduleGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void AppendEvent(ScheduleExecutionEvent evt) =>
        Dispatch(new AppendScheduleEvent(evt), static (s, a) => s with
        {
            Events = EventWindow.Append(s.Events, a.Event),
        });

    public void SetDays(int days) =>
        Dispatch(new SetScheduleDays(days), static (s, a) => s with { Days = a.Days });
}