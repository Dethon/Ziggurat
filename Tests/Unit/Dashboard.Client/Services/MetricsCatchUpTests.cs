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
using Domain.DTOs.Metrics.Enums;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Services;

public sealed class MetricsCatchUpTests : IDisposable
{
    private static readonly DateOnly _from = new(2026, 3, 1);
    private static readonly DateOnly _to = new(2026, 3, 2);

    private readonly FakeApiHandler _handler = new();
    private readonly TokensStore _tokensStore = new();
    private readonly ToolsStore _toolsStore = new();
    private readonly ErrorsStore _errorsStore = new();
    private readonly SchedulesStore _schedulesStore = new();
    private readonly MemoryStore _memoryStore = new();
    private readonly LatencyStore _latencyStore = new();
    private readonly VoiceStore _voiceStore = new();
    private readonly MetricsStore _metricsStore = new();
    private readonly HealthStore _healthStore = new();
    private readonly MetricFamilyTable _families;
    private readonly MetricsCatchUp _catchUp;

    public MetricsCatchUpTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        var api = new MetricsApiService(http);
        _families = new MetricFamilyTable(
            api, _tokensStore, _toolsStore, _errorsStore, _schedulesStore,
            _memoryStore, _latencyStore, _voiceStore);
        var overview = new OverviewFigures(api, _metricsStore, _healthStore);
        overview.SetDateRange(_from, _to);
        _catchUp = new MetricsCatchUp(_families, overview);

        _tokensStore.SetDateRange(_from, _to);
        _toolsStore.SetDateRange(_from, _to);
        _errorsStore.SetDateRange(_from, _to);
        _schedulesStore.SetDateRange(_from, _to);
        _memoryStore.SetDateRange(_from, _to);
        _latencyStore.SetDateRange(_from, _to);
        _voiceStore.SetDateRange(_from, _to);
    }

    public void Dispose()
    {
        _tokensStore.Dispose();
        _toolsStore.Dispose();
        _errorsStore.Dispose();
        _schedulesStore.Dispose();
        _memoryStore.Dispose();
        _latencyStore.Dispose();
        _voiceStore.Dispose();
        _metricsStore.Dispose();
        _healthStore.Dispose();
    }

    // Catch-up reloads every family whatever any one of them answers, and reports the failure to
    // its caller — the live connection, which logs it and stays live. Nothing here stages every
    // response, so these drive it through that same failure.
    private async Task CatchUpAsync()
    {
        try
        {
            await _catchUp.CatchUpAsync();
        }
        catch (HttpRequestException)
        {
            // A family with nothing staged answers 404 and keeps its last known values.
        }
    }

    // Every family, events and breakdown, over the range the family already holds. The assertion is
    // on what went out.
    public static TheoryData<string, string, string> FamilyRequests => new()
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
    [MemberData(nameof(FamilyRequests))]
    public async Task CatchUpAsync_AnyFamily_ReloadsItForTheRangeTheFamilyHolds(
        string _, string eventsRequest, string breakdownRequest)
    {
        var range = "from=2026-03-01&to=2026-03-02";

        await CatchUpAsync();

        _handler.Requests.ShouldContain(u => u != null && u.Contains(eventsRequest + range, StringComparison.Ordinal));
        _handler.Requests.ShouldContain(u => u != null && u.Contains(breakdownRequest + range, StringComparison.Ordinal));
    }

    // Recovering must not move the page under whoever is reading it.
    [Fact]
    public async Task CatchUpAsync_TheUserHasChosenGroupByMetricAndTime_LeavesEveryChoiceAlone()
    {
        _voiceStore.SetGroupBy(VoiceDimension.Identity);
        _voiceStore.SetMetric(VoiceMetric.SttLatencyMs);
        _voiceStore.SetAgg(Aggregation.P95);
        _tokensStore.SetGroupBy(TokenDimension.Model);
        _tokensStore.SetMetric(TokenMetric.Cost);

        await CatchUpAsync();

        _voiceStore.State.GroupBy.ShouldBe(VoiceDimension.Identity);
        _voiceStore.State.Metric.ShouldBe(VoiceMetric.SttLatencyMs);
        _voiceStore.State.Agg.ShouldBe(Aggregation.P95);
        _tokensStore.State.GroupBy.ShouldBe(TokenDimension.Model);
        _tokensStore.State.Metric.ShouldBe(TokenMetric.Cost);
        _tokensStore.State.From.ShouldBe(_from);
        _tokensStore.State.To.ShouldBe(_to);
    }

    // The Overview KPI row and the Service Health grid go as stale as the charts during an outage,
    // and neither is a metric family: a walk of the family table alone left both showing whatever
    // the page load managed, with no second chance at them. The KPI totals are added up from the
    // event lists this walk reloads, which is what keeps them on the same snapshot as the dedupe
    // question the release asks of those lists.
    [Fact]
    public async Task CatchUpAsync_TheSummaryAndHealthMovedDuringTheOutage_ReReadsThemIntoTheirStores()
    {
        _handler.AnswerFor("api/metrics/tokens?", new List<TokenUsagePayload>
        {
            new("nabu", "m", 120, 30, 1.5m),
        });
        _handler.AnswerFor("api/metrics/tokens/by/Model", new Dictionary<string, decimal>());
        _handler.AnswerFor("api/metrics/health", new List<ServiceHealthResponse>
        {
            new("agent", true, "2026-03-02T10:00:00Z"),
        });

        await CatchUpAsync();

        _metricsStore.State.InputTokens.ShouldBe(120);
        _metricsStore.State.OutputTokens.ShouldBe(30);
        _healthStore.State.Services.ShouldContain(s => s.Service == "agent" && s.IsHealthy);
    }

    private sealed record VoiceEventPayload(int Metric, string SatelliteId);

    private sealed record TokenUsagePayload(
        string Sender, string Model, int InputTokens, int OutputTokens, decimal Cost);
}