using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Web;

public record SetWebEvents(IReadOnlyList<ModalDismissalEvent> Events) : IAction;
public record AppendWebEvent(ModalDismissalEvent Event) : IAction;
public record SetWebTrend(IReadOnlyList<LatencyTrendSeries> Trend) : IAction;
public record SetWebBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetWebGroupBy(ModalDismissalDimension GroupBy) : IAction;
public record SetWebMetric(ModalDismissalMetric Metric) : IAction;
public record SetWebDateRange(DateOnly From, DateOnly To) : IAction;
public record SetWebAgg(Aggregation Agg) : IAction;

public sealed class WebStore : Store<WebState>
{
    public WebStore() : base(new WebState()) { }

    public void SetEvents(IReadOnlyList<ModalDismissalEvent> events) =>
        Dispatch(new SetWebEvents(events), static (s, a) => s with { Events = a.Events });

    public void AppendEvent(ModalDismissalEvent evt) =>
        Dispatch(new AppendWebEvent(evt), static (s, a) => s with { Events = EventWindow.Append(s.Events, a.Event) });

    public void SetTrend(IReadOnlyList<LatencyTrendSeries> trend) =>
        Dispatch(new SetWebTrend(trend), static (s, a) => s with { Trend = a.Trend });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetWebBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ModalDismissalDimension groupBy) =>
        Dispatch(new SetWebGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(ModalDismissalMetric metric) =>
        Dispatch(new SetWebMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetWebDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });

    public void SetAgg(Aggregation agg) =>
        Dispatch(new SetWebAgg(agg), static (s, a) => s with { Agg = a.Agg });
}