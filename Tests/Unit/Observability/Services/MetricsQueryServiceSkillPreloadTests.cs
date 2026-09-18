using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

// The skills family as the dashboard reads it: a breakdown by each dimension and metric, and one
// series per outcome over time.
public class MetricsQueryServiceSkillPreloadTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly MetricsQueryService _sut;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly DateOnly Date = new(2026, 9, 18);

    public MetricsQueryServiceSkillPreloadTests()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_db.Object);
        _sut = new MetricsQueryService(redis.Object);

        var at = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var entries = new MetricEvent[]
        {
            new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.Preloaded, AgentId = "nabu", Channel = "voice", Skills = ["home-assistant"], DurationMs = 300, InputTokens = 2100, Timestamp = at },
            new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.Preloaded, AgentId = "nabu", Channel = "voice", Skills = ["home-assistant", "home-watches"], DurationMs = 500, InputTokens = 2200, Timestamp = at },
            new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.Deadline, AgentId = "jonas", Channel = "signalr", DurationMs = 600, Timestamp = at.AddHours(1) },
            new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.None, AgentId = "jonas", Channel = "signalr", DurationMs = 200, InputTokens = 2000, Timestamp = at.AddHours(1) },
            new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.SkippedLemonade, AgentId = "jonas", Channel = "signalr", Timestamp = at.AddHours(1) }
        }.Select(e => new RedisValue(JsonSerializer.Serialize(e, _jsonOptions))).ToArray();

        _db.Setup(d => d.SortedSetRangeByScoreAsync("metrics:skills:2026-09-18", It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(entries);
    }

    [Fact]
    public async Task Grouped_ByOutcome_CountsEveryJudgment()
    {
        var result = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Outcome, SkillPreloadMetric.Count, Date, Date);

        result[SkillPreloadOutcomes.Preloaded].ShouldBe(2m);
        result[SkillPreloadOutcomes.Deadline].ShouldBe(1m);
        result[SkillPreloadOutcomes.None].ShouldBe(1m);
        result[SkillPreloadOutcomes.SkippedLemonade].ShouldBe(1m);
    }

    [Fact]
    public async Task Grouped_BySkill_CountsEachSkillPreloadedAndNothingElse()
    {
        var result = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Skill, SkillPreloadMetric.Count, Date, Date);

        result.ShouldBe(new Dictionary<string, decimal> { ["home-assistant"] = 2m, ["home-watches"] = 1m });
    }

    [Fact]
    public async Task Grouped_LatencyByAgent_AggregatesOverTheJudgmentsThatHaveOne()
    {
        var avg = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Agent, SkillPreloadMetric.LatencyMs, Date, Date);
        var max = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Agent, SkillPreloadMetric.LatencyMs, Date, Date, Aggregation.Max);

        avg["nabu"].ShouldBe(400m);
        avg["jonas"].ShouldBe(400m);
        max["jonas"].ShouldBe(600m);
    }

    [Fact]
    public async Task Grouped_InputTokensByChannel_SumsThem()
    {
        var result = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Channel, SkillPreloadMetric.InputTokens, Date, Date);

        result["voice"].ShouldBe(4300m);
        result["signalr"].ShouldBe(2000m);
    }

    [Fact]
    public async Task Trend_IsOneSeriesPerOutcome_CountedPerBucket()
    {
        var trend = await _sut.GetSkillPreloadTrendAsync(Date, Date);

        trend.Select(s => s.Stage).ShouldBe(["deadline", "none", "preloaded", "skipped-lemonade"]);
        var preloaded = trend.Single(s => s.Stage == SkillPreloadOutcomes.Preloaded);
        preloaded.Points.ShouldHaveSingleItem().Value.ShouldBe(2m);
        preloaded.Points[0].Bucket.Hour.ShouldBe(10);
        trend.Single(s => s.Stage == SkillPreloadOutcomes.Deadline).Points[0].Bucket.Hour.ShouldBe(11);
    }
}