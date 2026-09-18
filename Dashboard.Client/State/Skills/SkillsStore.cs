using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Skills;

public record SetSkillsEvents(IReadOnlyList<SkillPreloadEvent> Events) : IAction;
public record AppendSkillsEvent(SkillPreloadEvent Event) : IAction;
public record SetSkillsTrend(IReadOnlyList<LatencyTrendSeries> Trend) : IAction;
public record SetSkillsBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetSkillsGroupBy(SkillPreloadDimension GroupBy) : IAction;
public record SetSkillsMetric(SkillPreloadMetric Metric) : IAction;
public record SetSkillsDateRange(DateOnly From, DateOnly To) : IAction;
public record SetSkillsAgg(Aggregation Agg) : IAction;

public sealed class SkillsStore : Store<SkillsState>
{
    public SkillsStore() : base(new SkillsState()) { }

    public void SetEvents(IReadOnlyList<SkillPreloadEvent> events) =>
        Dispatch(new SetSkillsEvents(events), static (s, a) => s with { Events = a.Events });

    public void AppendEvent(SkillPreloadEvent evt) =>
        Dispatch(new AppendSkillsEvent(evt), static (s, a) => s with { Events = EventWindow.Append(s.Events, a.Event) });

    public void SetTrend(IReadOnlyList<LatencyTrendSeries> trend) =>
        Dispatch(new SetSkillsTrend(trend), static (s, a) => s with { Trend = a.Trend });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetSkillsBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(SkillPreloadDimension groupBy) =>
        Dispatch(new SetSkillsGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(SkillPreloadMetric metric) =>
        Dispatch(new SetSkillsMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetSkillsDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });

    public void SetAgg(Aggregation agg) =>
        Dispatch(new SetSkillsAgg(agg), static (s, a) => s with { Agg = a.Agg });
}