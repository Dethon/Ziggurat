using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Memory;

public record SetMemoryRecallEvents(IReadOnlyList<MemoryRecallEvent> Events) : IAction;
public record SetMemoryExtractionEvents(IReadOnlyList<MemoryExtractionEvent> Events) : IAction;
public record SetMemoryDreamingEvents(IReadOnlyList<MemoryDreamingEvent> Events) : IAction;
public record SetMemoryJudgmentEvents(IReadOnlyList<MemoryJudgmentEvent> Events) : IAction;
public record SetMemoryBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetMemoryGroupBy(MemoryDimension GroupBy) : IAction;
public record SetMemoryMetric(MemoryMetric Metric) : IAction;
public record AppendMemoryRecallEvent(MemoryRecallEvent Event) : IAction;
public record AppendMemoryExtractionEvent(MemoryExtractionEvent Event) : IAction;
public record AppendMemoryDreamingEvent(MemoryDreamingEvent Event) : IAction;
public record AppendMemoryJudgmentEvent(MemoryJudgmentEvent Event) : IAction;
public record SetMemoryDateRange(DateOnly From, DateOnly To) : IAction;

public sealed class MemoryStore : Store<MemoryState>
{
    public MemoryStore() : base(new MemoryState()) { }

    public void SetRecallEvents(IReadOnlyList<MemoryRecallEvent> events) =>
        Dispatch(new SetMemoryRecallEvents(events), static (s, a) => s with { RecallEvents = a.Events });

    public void SetExtractionEvents(IReadOnlyList<MemoryExtractionEvent> events) =>
        Dispatch(new SetMemoryExtractionEvents(events), static (s, a) => s with { ExtractionEvents = a.Events });

    public void SetDreamingEvents(IReadOnlyList<MemoryDreamingEvent> events) =>
        Dispatch(new SetMemoryDreamingEvents(events), static (s, a) => s with { DreamingEvents = a.Events });

    public void SetJudgmentEvents(IReadOnlyList<MemoryJudgmentEvent> events) =>
        Dispatch(new SetMemoryJudgmentEvents(events), static (s, a) => s with { JudgmentEvents = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetMemoryBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(MemoryDimension groupBy) =>
        Dispatch(new SetMemoryGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(MemoryMetric metric) =>
        Dispatch(new SetMemoryMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendRecallEvent(MemoryRecallEvent evt) =>
        Dispatch(new AppendMemoryRecallEvent(evt), static (s, a) => s with
        {
            RecallEvents = EventWindow.Append(s.RecallEvents, a.Event),
        });

    public void AppendExtractionEvent(MemoryExtractionEvent evt) =>
        Dispatch(new AppendMemoryExtractionEvent(evt), static (s, a) => s with
        {
            ExtractionEvents = EventWindow.Append(s.ExtractionEvents, a.Event),
        });

    public void AppendDreamingEvent(MemoryDreamingEvent evt) =>
        Dispatch(new AppendMemoryDreamingEvent(evt), static (s, a) => s with
        {
            DreamingEvents = EventWindow.Append(s.DreamingEvents, a.Event),
        });

    public void AppendJudgmentEvent(MemoryJudgmentEvent evt) =>
        Dispatch(new AppendMemoryJudgmentEvent(evt), static (s, a) => s with
        {
            JudgmentEvents = EventWindow.Append(s.JudgmentEvents, a.Event),
        });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetMemoryDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
}