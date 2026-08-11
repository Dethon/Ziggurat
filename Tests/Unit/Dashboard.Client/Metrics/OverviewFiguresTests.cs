using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Metrics;

public sealed class OverviewFiguresTests : IDisposable
{
    private readonly FakeApiHandler _handler = new();
    private readonly MetricsStore _metricsStore = new();
    private readonly HealthStore _healthStore = new();
    private readonly OverviewFigures _overview;

    public OverviewFiguresTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") };
        _overview = new OverviewFigures(new MetricsApiService(http), _metricsStore, _healthStore);
    }

    public void Dispose()
    {
        _metricsStore.Dispose();
        _healthStore.Dispose();
    }

    private static MetricsSummary Summary(long inputTokens) => new(
        InputTokens: inputTokens, OutputTokens: 0, TotalTokens: inputTokens,
        Cost: 0, ToolCalls: 0, ToolErrors: 0);

    // The KPI row's half of the same overlapping-load defect the families have: the thirty-day
    // summary is the slower request and used to land last, so the KPI row showed thirty days of
    // totals under a Today header.
    [Fact]
    public async Task LoadSummaryAsync_AnOlderReadAnswersLast_ItsTotalsAreNotApplied()
    {
        _handler.EnqueueResponse(Summary(3000), TimeSpan.FromMilliseconds(200));
        _handler.EnqueueResponse(Summary(10), TimeSpan.Zero);

        _overview.SetDateRange(new DateOnly(2026, 2, 23), new DateOnly(2026, 3, 24));
        var older = _overview.LoadSummaryAsync();
        while (_handler.Requests.IsEmpty)
        {
            await Task.Delay(1);
        }

        _overview.SetDateRange(new DateOnly(2026, 3, 24), new DateOnly(2026, 3, 24));
        await _overview.LoadSummaryAsync();
        await older;

        _metricsStore.State.InputTokens.ShouldBe(10);
    }
}