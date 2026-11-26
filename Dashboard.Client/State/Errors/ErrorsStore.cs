using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Errors;

public record SetErrorEvents(IReadOnlyList<ErrorEvent> Events) : IAction;
public record SetErrorBreakdown(Dictionary<string, int> Breakdown) : IAction;
public record SetErrorGroupBy(ErrorDimension GroupBy) : IAction;
public record AppendErrorEvent(ErrorEvent Event) : IAction;
public record SetErrorDays(int Days) : IAction;

public sealed class ErrorsStore : Store<ErrorsState>
{
    public ErrorsStore() : base(new ErrorsState()) { }

    public void SetEvents(IReadOnlyList<ErrorEvent> events) =>
        Dispatch(new SetErrorEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, int> breakdown) =>
        Dispatch(new SetErrorBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ErrorDimension groupBy) =>
        Dispatch(new SetErrorGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void AppendEvent(ErrorEvent evt) =>
        Dispatch(new AppendErrorEvent(evt), static (s, a) => s with
        {
            Events = EventWindow.Append(s.Events, a.Event),
        });

    public void SetDays(int days) =>
        Dispatch(new SetErrorDays(days), static (s, a) => s with { Days = a.Days });
}