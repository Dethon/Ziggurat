using System.Reactive.Linq;
using Dashboard.Client.Effects;
using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Effects;

public class MetricsHubBinderTests : IAsyncDisposable
{
    private readonly FakeMetricsHubConnection _hub = new();
    private readonly FakeApiHandler _handler = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MetricsStore _metricsStore = new();
    private readonly HealthStore _healthStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricsApiService _api;
    private readonly MetricFamilyTable _families;
    private readonly MetricsHubBinder _binder;

    public MetricsHubBinderTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        _api = new MetricsApiService(http);
        _families = new MetricFamilyTable(
            _api, _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore);
        _binder = new MetricsHubBinder(_families, _metricsStore, _healthStore, NullLogger<MetricsHubBinder>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        _binder.Unbind();
        _tokensStore.Dispose();
        _toolsStore.Dispose();
        _errorsStore.Dispose();
        _schedulesStore.Dispose();
        _metricsStore.Dispose();
        _healthStore.Dispose();
        _memoryStore.Dispose();
        _latencyStore.Dispose();
        _voiceStore.Dispose();
        return ValueTask.CompletedTask;
    }

    private static readonly DateOnly _from = new(2026, 3, 1);
    private static readonly DateOnly _to = new(2026, 3, 2);

    // Every family, in the general form of the aggregation bug: whatever the store holds is what
    // goes out, and whatever comes back is what the family charts.
    private static readonly Dictionary<
        string,
        (Func<MetricFamilyTable, MetricFamily> Family,
         Action<MetricsHubBinderTests> Choose,
         string ExpectedRequest,
         object Breakdown,
         Func<MetricsHubBinderTests, object?> GetBreakdown,
         object? SecondResponse)>
    _familyCases = new()
    {
        ["tokens"] = (
            t => t.Tokens,
            self =>
            {
                self._tokensStore.SetGroupBy(TokenDimension.Model);
                self._tokensStore.SetMetric(TokenMetric.Cost);
            },
            "api/metrics/tokens/by/Model?metric=Cost&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["gpt"] = 12m },
            self => self._tokensStore.State.Breakdown,
            null),
        ["tools"] = (
            t => t.Tools,
            self =>
            {
                self._toolsStore.SetGroupBy(ToolDimension.Status);
                self._toolsStore.SetMetric(ToolMetric.AvgDuration);
            },
            "api/metrics/tools/by/Status?metric=AvgDuration&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["ok"] = 30m },
            self => self._toolsStore.State.Breakdown,
            null),
        ["errors"] = (
            t => t.Errors,
            self => self._errorsStore.SetGroupBy(ErrorDimension.ErrorType),
            "api/metrics/errors/by/ErrorType?from=2026-03-01&to=2026-03-02",
            new Dictionary<string, int> { ["timeout"] = 4 },
            self => self._errorsStore.State.Breakdown,
            null),
        ["schedules"] = (
            t => t.Schedules,
            self => self._schedulesStore.SetGroupBy(ScheduleDimension.Status),
            "api/metrics/schedules/by/Status?from=2026-03-01&to=2026-03-02",
            new Dictionary<string, int> { ["ok"] = 9 },
            self => self._schedulesStore.State.Breakdown,
            null),
        ["memory"] = (
            t => t.Memory,
            self =>
            {
                self._memoryStore.SetGroupBy(MemoryDimension.Agent);
                self._memoryStore.SetMetric(MemoryMetric.AvgDuration);
            },
            "api/metrics/memory/by/Agent?metric=AvgDuration&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["nabu"] = 80m },
            self => self._memoryStore.State.Breakdown,
            null),
        ["latency"] = (
            t => t.Latency,
            self =>
            {
                self._latencyStore.SetGroupBy(LatencyDimension.Model);
                self._latencyStore.SetMetric(Aggregation.P99);
            },
            "api/metrics/latency/by/Model?metric=P99&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["m1"] = 900m },
            self => self._latencyStore.State.Breakdown,
            new List<LatencyTrendSeries>()),
        ["voice"] = (
            t => t.Voice,
            self =>
            {
                self._voiceStore.SetGroupBy(VoiceDimension.Identity);
                self._voiceStore.SetMetric(VoiceMetric.SttLatencyMs);
                self._voiceStore.SetAgg(Aggregation.P95);
            },
            "api/metrics/voice/by/Identity?metric=SttLatencyMs&agg=P95&from=2026-03-01&to=2026-03-02",
            new Dictionary<string, decimal> { ["frank"] = 210m },
            self => self._voiceStore.State.Breakdown,
            null),
    };

    public static TheoryData<string> FamilyNames => new(_familyCases.Keys);

    [Theory]
    [MemberData(nameof(FamilyNames))]
    public async Task RefreshAsync_AnyFamily_SendsEveryChoiceInItsStoreAndWritesTheBreakdown(string name)
    {
        var (family, choose, expectedRequest, breakdown, getBreakdown, secondResponse) = _familyCases[name];
        SetDateRangeOnEveryStore();
        choose(this);
        _handler.EnqueueResponse(breakdown, delay: TimeSpan.Zero);
        if (secondResponse is not null)
        {
            _handler.EnqueueResponse(secondResponse, delay: TimeSpan.Zero);
        }

        await family(_families).RefreshAsync();

        _handler.Requests.ShouldContain(u => u != null && u.EndsWith(expectedRequest, StringComparison.Ordinal));
        getBreakdown(this).ShouldBe(breakdown);
    }

    private DataLoadEffect NewDataLoadEffect() =>
        new(_families, new OverviewFigures(_api, _metricsStore, _healthStore), _binder);

    // A page load, per family: the events request and the breakdown request, both carrying the
    // range the load was given. Nothing is staged, so every response is a 404 the effect swallows;
    // the assertion is on what went out.
    public static TheoryData<string, string, string> PageLoadRequests => new()
    {
        { "tokens", "api/metrics/tokens?", "api/metrics/tokens/by/User?metric=Tokens&" },
        { "tools", "api/metrics/tools?", "api/metrics/tools/by/ToolName?metric=CallCount&" },
        { "errors", "api/metrics/errors/range?", "api/metrics/errors/by/Service?" },
        { "schedules", "api/metrics/schedules?", "api/metrics/schedules/by/Schedule?" },
        { "memory", "api/metrics/memory/recall?", "api/metrics/memory/by/User?metric=Count&" },
        { "latency", "api/metrics/latency?", "api/metrics/latency/by/Stage?metric=P95&" },
        { "voice", "api/metrics/voice?", "api/metrics/voice/by/SatelliteId?metric=UtteranceTranscribed&agg=Avg&" },
    };

    [Theory]
    [MemberData(nameof(PageLoadRequests))]
    public async Task LoadAsync_AnyFamily_IssuesItsEventsAndBreakdownRequestsForTheGivenRange(
        string _, string eventsRequest, string breakdownRequest)
    {
        var range = "from=2026-03-01&to=2026-03-02";

        await NewDataLoadEffect().LoadAsync(_from, _to, _families.All);

        _handler.Requests.ShouldContain(u => u != null && u.Contains(eventsRequest + range, StringComparison.Ordinal));
        _handler.Requests.ShouldContain(u => u != null && u.Contains(breakdownRequest + range, StringComparison.Ordinal));
    }

    private void SetDateRangeOnEveryStore()
    {
        _tokensStore.SetDateRange(_from, _to);
        _toolsStore.SetDateRange(_from, _to);
        _errorsStore.SetDateRange(_from, _to);
        _schedulesStore.SetDateRange(_from, _to);
        _memoryStore.SetDateRange(_from, _to);
        _latencyStore.SetDateRange(_from, _to);
        _voiceStore.SetDateRange(_from, _to);
    }

    private static readonly
    Dictionary<
        string,
        (object StaleData, object FreshData, Func<FakeMetricsHubConnection, Task> FireEvent, Func<MetricsHubBinderTests, object?> GetBreakdown)>
    _rapidEventCases = new()
    {
        ["TokenUsage"] = (
            new Dictionary<string, decimal> { ["stale-model"] = 100m },
            new Dictionary<string, decimal> { ["fresh-model"] = 200m },
            hub => hub.RaiseAsync("OnTokenUsage", new TokenUsageEvent
            { Sender = "test", Model = "m", InputTokens = 1, OutputTokens = 1, Cost = 0.01m }),
            self => self._tokensStore.State.Breakdown),
        ["ToolCall"] = (
            new Dictionary<string, decimal> { ["stale-tool"] = 10m },
            new Dictionary<string, decimal> { ["fresh-tool"] = 20m },
            hub => hub.RaiseAsync("OnToolCall", new ToolCallEvent
            { ToolName = "t", Success = true, DurationMs = 100 }),
            self => self._toolsStore.State.Breakdown),
        ["Error"] = (
            new Dictionary<string, int> { ["stale-err"] = 5 },
            new Dictionary<string, int> { ["fresh-err"] = 10 },
            hub => hub.RaiseAsync("OnError", new ErrorEvent
            { Message = "err", Service = "s", ErrorType = "e" }),
            self => self._errorsStore.State.Breakdown),
        ["ScheduleExecution"] = (
            new Dictionary<string, int> { ["stale-sched"] = 3 },
            new Dictionary<string, int> { ["fresh-sched"] = 7 },
            hub => hub.RaiseAsync("OnScheduleExecution", new ScheduleExecutionEvent
            { ScheduleId = "s", Prompt = "p", Success = true, DurationMs = 50 }),
            self => self._schedulesStore.State.Breakdown),
        ["MemoryRecall"] = (
            new Dictionary<string, decimal> { ["stale-memory"] = 50m },
            new Dictionary<string, decimal> { ["fresh-memory"] = 100m },
            hub => hub.RaiseAsync("OnMemoryRecall", new MemoryRecallEvent
            { DurationMs = 100, MemoryCount = 5, UserId = "test" }),
            self => self._memoryStore.State.Breakdown),
        ["MemoryExtraction"] = (
            new Dictionary<string, decimal> { ["stale-extract"] = 30m },
            new Dictionary<string, decimal> { ["fresh-extract"] = 60m },
            hub => hub.RaiseAsync("OnMemoryExtraction", new MemoryExtractionEvent
            { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "test" }),
            self => self._memoryStore.State.Breakdown),
        ["MemoryDreaming"] = (
            new Dictionary<string, decimal> { ["stale-dream"] = 10m },
            new Dictionary<string, decimal> { ["fresh-dream"] = 20m },
            hub => hub.RaiseAsync("OnMemoryDreaming", new MemoryDreamingEvent
            { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "test" }),
            self => self._memoryStore.State.Breakdown),
    };

    public static TheoryData<string> RapidEventCaseNames => new(_rapidEventCases.Keys);

    // Five events arriving during one outstanding response cost the observability service two
    // aggregations, not five: the run in flight is shared and repeats once for the state that moved
    // under it.
    [Theory]
    [MemberData(nameof(RapidEventCaseNames))]
    public async Task RefreshAsync_EventsArriveWhileAResponseIsOutstanding_CoalescesIntoTwoRequests(string caseName)
    {
        var (staleData, freshData, fireEvent, getBreakdown) = _rapidEventCases[caseName];

        _binder.Bind(_hub);

        _handler.EnqueueResponse(staleData, delay: TimeSpan.FromMilliseconds(500));
        _handler.EnqueueResponse(freshData, delay: TimeSpan.FromMilliseconds(10));

        var fires = Enumerable.Range(0, 5).Select(_ => fireEvent(_hub)).ToArray();
        await Task.WhenAll(fires);

        _handler.Requests.Count.ShouldBe(2);
        getBreakdown(this).ShouldBe(freshData);
    }

    private Task RaiseVoiceAsync(string satelliteId) =>
        _hub.RaiseAsync("OnVoice", new VoiceEvent
        {
            Metric = VoiceMetric.UtteranceTranscribed,
            SatelliteId = satelliteId,
        });

    // A reconnect can land while a catch-up is still holding pushes. The second hold must not
    // discard what the first one captured, and the first release must not deliver anything the
    // overlapping hold still wants held.
    [Fact]
    public async Task ReleaseHeldPushesAsync_ASecondHoldOverlappedTheFirst_NothingIsDroppedAndOrderHolds()
    {
        _binder.Bind(_hub);
        _binder.HoldPushes();
        await RaiseVoiceAsync("held-1");
        _binder.HoldPushes();
        await RaiseVoiceAsync("held-2");

        await _binder.ReleaseHeldPushesAsync();
        _voiceStore.State.Events.ShouldBeEmpty();

        await _binder.ReleaseHeldPushesAsync();
        _voiceStore.State.Events.Select(e => e.SatelliteId).ShouldBe(["held-1", "held-2"]);
    }

    // Release delivers the held events one awaited apply at a time. A push arriving in one of those
    // gaps must land behind the rest of the queue, or the TakeLast tables show events out of order.
    [Fact]
    public async Task ReleaseHeldPushesAsync_APushArrivesMidRelease_LandsBehindTheHeldEvents()
    {
        _binder.Bind(_hub);
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.FromMilliseconds(200));
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        _binder.HoldPushes();
        await RaiseVoiceAsync("held-1");
        await RaiseVoiceAsync("held-2");

        var release = _binder.ReleaseHeldPushesAsync();
        await RaiseVoiceAsync("fresh");
        await release;

        _voiceStore.State.Events.Select(e => e.SatelliteId).ShouldBe(["held-1", "held-2", "fresh"]);
    }

    // A store subscriber is a rendered component, and one of those throwing used to take the whole
    // drain down: the queue stayed in place with nothing left to drain it, so every later push was
    // enqueued and live updates stopped for good. The throw also escaped the release, through
    // DataLoadEffect's finally and out of ConnectAsync, which catches nothing.
    [Fact]
    public async Task ReleaseHeldPushesAsync_AnApplyThrows_TheRestAreDeliveredAndPushesFlowAgain()
    {
        _binder.Bind(_hub);
        _binder.HoldPushes();
        await RaiseVoiceAsync("held-1");
        await RaiseVoiceAsync("held-2");

        var throwsLeft = 1;
        using var subscription = _voiceStore.StateObservable.Skip(1).Subscribe(_ =>
        {
            if (throwsLeft-- > 0)
            {
                throw new InvalidOperationException("a subscribed component blew up");
            }
        });

        await Should.NotThrowAsync(() => _binder.ReleaseHeldPushesAsync());
        await RaiseVoiceAsync("fresh");

        _voiceStore.State.Events.Select(e => e.SatelliteId).ShouldBe(["held-1", "held-2", "fresh"]);
    }

    // Unbinding is how a module lets go. Leaving the hold behind kept a queue of closures holding
    // store references alive, and the next binding started out holding pushes nobody had asked to
    // hold.
    [Fact]
    public async Task Unbind_PushesWereStillHeld_LetsGoOfTheQueue()
    {
        _binder.Bind(_hub);
        _binder.HoldPushes();
        await RaiseVoiceAsync("held-1");

        _binder.Unbind();
        _binder.Bind(_hub);
        await RaiseVoiceAsync("fresh");

        _voiceStore.State.Events.Select(e => e.SatelliteId).ShouldBe(["fresh"]);
    }

    [Fact]
    public async Task LiveEvent_RefreshFails_LeavesTheBreakdownAtItsLastKnownValue()
    {
        var lastKnown = new Dictionary<string, decimal> { ["kept"] = 42m };
        _tokensStore.SetBreakdown(lastKnown);
        _binder.Bind(_hub);

        // Nothing is staged, so the handler answers 404 and the refresh throws.
        await _hub.RaiseAsync("OnTokenUsage", new TokenUsageEvent
        { Sender = "test", Model = "m", InputTokens = 1, OutputTokens = 1, Cost = 0.01m });

        _tokensStore.State.Breakdown.ShouldBe(lastKnown);
    }

    [Fact]
    public async Task OnLatency_AppendsEventToLatencyStore()
    {
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        _handler.EnqueueResponse(new List<LatencyTrendSeries>(), delay: TimeSpan.Zero);
        _binder.Bind(_hub);

        await _hub.RaiseAsync("OnLatency", new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 5 });

        _latencyStore.State.Events.ShouldContain(e => e.Stage == LatencyStage.LlmTotal);
    }

    [Fact]
    public async Task OnVoice_RequestsBreakdownUsingStoreAgg()
    {
        // Regression guard for the P95-pill-but-Avg-chart bug: a live voice event must refetch
        // the breakdown using whatever aggregation the user picked, not silently fall back to Avg.
        _voiceStore.SetAgg(Aggregation.P95);
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), delay: TimeSpan.Zero);
        _binder.Bind(_hub);

        await _hub.RaiseAsync("OnVoice", new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = "kitchen-01" });

        _handler.LastRequestUri.ShouldNotBeNull();
        _handler.LastRequestUri!.ShouldContain("agg=P95");
    }

    [Fact]
    public async Task LoadAsync_RequestsVoiceBreakdownUsingStoreAgg()
    {
        // DataLoadEffect is a third, independent call site for GetVoiceGroupedAsync (the page-load
        // path, distinct from MetricsHubBinder's live-refresh path) that can silently omit `agg`
        // and fall back to Avg. No response staging is needed: we only assert on the outbound
        // request, and DataLoadEffect swallows the resulting 404s from the unstaffed FakeApiHandler.
        _voiceStore.SetAgg(Aggregation.P95);

        await NewDataLoadEffect().LoadAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 24), _families.All);

        _handler.Requests.ShouldContain(u => u != null && u.Contains("voice/by") && u.Contains("agg=P95"));
    }
}