using Dashboard.Client.State;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

// A dashboard tab left open collects every push since the last full load, and nothing used to take
// an event off any of these lists. Every store shares the one bounded append, so every list is
// checked against it here rather than one store at a time.
public sealed class AppendedEventsTests : IDisposable
{
    private readonly TokensStore _tokens = new();
    private readonly ToolsStore _tools = new();
    private readonly ErrorsStore _errors = new();
    private readonly SchedulesStore _schedules = new();
    private readonly MemoryStore _memory = new();
    private readonly LatencyStore _latency = new();
    private readonly VoiceStore _voice = new();

    public void Dispose()
    {
        _tokens.Dispose();
        _tools.Dispose();
        _errors.Dispose();
        _schedules.Dispose();
        _memory.Dispose();
        _latency.Dispose();
        _voice.Dispose();
    }

    public static TheoryData<string> Lists =>
    [
        "tokens", "tools", "errors", "schedules",
        "memory.recall", "memory.extraction", "memory.dreaming", "memory.judgments",
        "latency", "voice",
    ];

    private sealed record EventList(Action<int> Load, Action Append, Func<int> Count);

    private EventList ListNamed(string name) => name switch
    {
        "tokens" => new EventList(
            count => _tokens.SetEvents([.. Enumerable.Range(0, count).Select(_ => Token())]),
            () => _tokens.AppendEvent(Token()),
            () => _tokens.State.Events.Count),
        "tools" => new EventList(
            count => _tools.SetEvents([.. Enumerable.Range(0, count).Select(_ => Tool())]),
            () => _tools.AppendEvent(Tool()),
            () => _tools.State.Events.Count),
        "errors" => new EventList(
            count => _errors.SetEvents([.. Enumerable.Range(0, count).Select(_ => Error())]),
            () => _errors.AppendEvent(Error()),
            () => _errors.State.Events.Count),
        "schedules" => new EventList(
            count => _schedules.SetEvents([.. Enumerable.Range(0, count).Select(_ => Schedule())]),
            () => _schedules.AppendEvent(Schedule()),
            () => _schedules.State.Events.Count),
        "memory.recall" => new EventList(
            count => _memory.SetRecallEvents([.. Enumerable.Range(0, count).Select(_ => Recall())]),
            () => _memory.AppendRecallEvent(Recall()),
            () => _memory.State.RecallEvents.Count),
        "memory.extraction" => new EventList(
            count => _memory.SetExtractionEvents([.. Enumerable.Range(0, count).Select(_ => Extraction())]),
            () => _memory.AppendExtractionEvent(Extraction()),
            () => _memory.State.ExtractionEvents.Count),
        "memory.dreaming" => new EventList(
            count => _memory.SetDreamingEvents([.. Enumerable.Range(0, count).Select(_ => Dreaming())]),
            () => _memory.AppendDreamingEvent(Dreaming()),
            () => _memory.State.DreamingEvents.Count),
        "memory.judgments" => new EventList(
            count => _memory.SetJudgmentEvents([.. Enumerable.Range(0, count).Select(_ => Judgment())]),
            () => _memory.AppendJudgmentEvent(Judgment()),
            () => _memory.State.JudgmentEvents.Count),
        "latency" => new EventList(
            count => _latency.SetEvents([.. Enumerable.Range(0, count).Select(_ => Latency())]),
            () => _latency.AppendEvent(Latency()),
            () => _latency.State.Events.Count),
        "voice" => new EventList(
            count => _voice.SetEvents([.. Enumerable.Range(0, count).Select(_ => Voice())]),
            () => _voice.AppendEvent(Voice()),
            () => _voice.State.Events.Count),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Lists))]
    public void AppendEvent_ThePushesKeepComing_TheListStopsAtTheCap(string name)
    {
        var list = ListNamed(name);

        Enumerable.Range(0, EventWindow.Cap + 50).ToList().ForEach(_ => list.Append());

        list.Count().ShouldBe(EventWindow.Cap);
    }

    // A thirty-day load can answer with more events than the cap, and that is the range the user
    // asked for: the first push after it must not throw the rest of the month away.
    [Theory]
    [MemberData(nameof(Lists))]
    public void AppendEvent_TheLoadAnsweredWithMoreThanTheCap_ThePushDoesNotShortenTheList(string name)
    {
        var list = ListNamed(name);
        list.Load(EventWindow.Cap + 100);

        list.Append();

        list.Count().ShouldBe(EventWindow.Cap + 100);
    }

    private static TokenUsageEvent Token() => new()
    {
        Sender = "nabu",
        Model = "m",
        InputTokens = 1,
        OutputTokens = 1,
        Cost = 0.01m,
    };

    private static ToolCallEvent Tool() => new() { ToolName = "t", DurationMs = 1, Success = true };

    private static ErrorEvent Error() => new() { Service = "agent", ErrorType = "e", Message = "m" };

    private static ScheduleExecutionEvent Schedule() => new()
    {
        ScheduleId = "s",
        Prompt = "p",
        DurationMs = 1,
        Success = true,
    };

    private static MemoryRecallEvent Recall() => new() { DurationMs = 1, MemoryCount = 1, UserId = "u" };

    private static MemoryExtractionEvent Extraction() => new()
    {
        DurationMs = 1,
        CandidateCount = 1,
        StoredCount = 1,
        UserId = "u",
    };

    private static MemoryDreamingEvent Dreaming() => new()
    {
        MergedCount = 1,
        DecayedCount = 1,
        ProfileRegenerated = false,
        UserId = "u",
    };

    private static MemoryJudgmentEvent Judgment() => new() { Kind = MemoryJudgmentKinds.Gate, UserId = "u", Answered = true };

    private static LatencyEvent Latency() => new() { Stage = LatencyStage.LlmTotal, DurationMs = 1 };

    private static VoiceEvent Voice() => new() { Metric = VoiceMetric.UtteranceTranscribed };
}