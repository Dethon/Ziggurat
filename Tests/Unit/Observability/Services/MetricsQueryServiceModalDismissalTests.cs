using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

// The web family as the dashboard reads it: a breakdown by kind or outcome, and one series per
// outcome over time — the miss rate as a number.
public class MetricsQueryServiceModalDismissalTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly MetricsQueryService _sut;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly DateOnly Date = new(2026, 9, 18);

    public MetricsQueryServiceModalDismissalTests()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_db.Object);
        _sut = new MetricsQueryService(redis.Object);

        var at = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var entries = new MetricEvent[]
        {
            new ModalDismissalEvent { Kind = ModalKinds.Cookie, Outcome = ModalDismissalOutcomes.Selector, Selector = "#onetrust-accept-btn-handler", Timestamp = at },
            new ModalDismissalEvent { Kind = ModalKinds.Cookie, Outcome = ModalDismissalOutcomes.Judgment, Selector = "judgment(2)", Confidence = 0.9, DurationMs = 300, Timestamp = at },
            new ModalDismissalEvent { Kind = ModalKinds.Cookie, Outcome = ModalDismissalOutcomes.LeftStanding, DurationMs = 500, Timestamp = at.AddHours(1) },
            new ModalDismissalEvent { Kind = ModalKinds.Newsletter, Outcome = ModalDismissalOutcomes.Text, Selector = "text(no thanks)", Timestamp = at.AddHours(1) },
            new ModalDismissalEvent { Kind = ModalKinds.Newsletter, Outcome = ModalDismissalOutcomes.LeftStanding, Timestamp = at.AddHours(1) }
        }.Select(e => new RedisValue(JsonSerializer.Serialize(e, _jsonOptions))).ToArray();

        _db.Setup(d => d.SortedSetRangeByScoreAsync("metrics:modals:2026-09-18", It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(entries);
    }

    [Fact]
    public async Task Grouped_ByOutcome_CountsEveryOverlay()
    {
        var result = await _sut.GetModalDismissalGroupedAsync(ModalDismissalDimension.Outcome, ModalDismissalMetric.Count, Date, Date);

        result[ModalDismissalOutcomes.Selector].ShouldBe(1m);
        result[ModalDismissalOutcomes.Text].ShouldBe(1m);
        result[ModalDismissalOutcomes.Judgment].ShouldBe(1m);
        result[ModalDismissalOutcomes.LeftStanding].ShouldBe(2m);
    }

    [Fact]
    public async Task Grouped_ByKind_CountsEveryOverlay()
    {
        var result = await _sut.GetModalDismissalGroupedAsync(ModalDismissalDimension.Kind, ModalDismissalMetric.Count, Date, Date);

        result.ShouldBe(new Dictionary<string, decimal> { [ModalKinds.Cookie] = 3m, [ModalKinds.Newsletter] = 2m });
    }

    [Fact]
    public async Task Grouped_LatencyByKind_AggregatesOverTheOverlaysAJudgeWasAskedAbout()
    {
        var avg = await _sut.GetModalDismissalGroupedAsync(ModalDismissalDimension.Kind, ModalDismissalMetric.LatencyMs, Date, Date);
        var max = await _sut.GetModalDismissalGroupedAsync(ModalDismissalDimension.Kind, ModalDismissalMetric.LatencyMs, Date, Date, Aggregation.Max);

        avg[ModalKinds.Cookie].ShouldBe(400m);
        max[ModalKinds.Cookie].ShouldBe(500m);
        avg[ModalKinds.Newsletter].ShouldBe(0m);
    }

    [Fact]
    public async Task Trend_IsOneSeriesPerOutcome_CountedPerBucket()
    {
        var trend = await _sut.GetModalDismissalTrendAsync(Date, Date);

        trend.Select(s => s.Stage).ShouldBe(["judgment", "left-standing", "selector", "text"]);
        var leftStanding = trend.Single(s => s.Stage == ModalDismissalOutcomes.LeftStanding);
        leftStanding.Points.ShouldHaveSingleItem().Value.ShouldBe(2m);
        leftStanding.Points[0].Bucket.Hour.ShouldBe(11);
        trend.Single(s => s.Stage == ModalDismissalOutcomes.Selector).Points[0].Bucket.Hour.ShouldBe(10);
    }

    [Fact]
    public async Task Trend_ForOneKind_CountsThatKindAlone()
    {
        var trend = await _sut.GetModalDismissalTrendAsync(Date, Date, ModalKinds.Newsletter);

        trend.Select(s => s.Stage).ShouldBe(["left-standing", "text"]);
        trend.Single(s => s.Stage == ModalDismissalOutcomes.LeftStanding).Points.ShouldHaveSingleItem().Value.ShouldBe(1m);
    }
}