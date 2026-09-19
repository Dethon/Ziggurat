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

    private static readonly DateOnly _date = new(2026, 9, 18);

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
        var result = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Outcome, SkillPreloadMetric.Count, _date, _date);

        result[SkillPreloadOutcomes.Preloaded].ShouldBe(2m);
        result[SkillPreloadOutcomes.Deadline].ShouldBe(1m);
        result[SkillPreloadOutcomes.None].ShouldBe(1m);
        result[SkillPreloadOutcomes.SkippedLemonade].ShouldBe(1m);
    }

    [Fact]
    public async Task Grouped_BySkill_CountsEachSkillPreloadedAndNothingElse()
    {
        var result = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Skill, SkillPreloadMetric.Count, _date, _date);

        result.ShouldBe(new Dictionary<string, decimal> { ["home-assistant"] = 2m, ["home-watches"] = 1m });
    }

    // One judgment's tokens belong to the judgment. Summing the whole event under each skill it
    // preloaded made one 2,200-token call into two bars of 2,200, so the bars totalled more than
    // the pass spent; split, they still add up to it.
    [Fact]
    public async Task Grouped_InputTokensBySkill_SplitsAJudgmentAcrossTheSkillsItPreloaded()
    {
        var result = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Skill, SkillPreloadMetric.InputTokens, _date, _date);

        // 2,100 to home-assistant alone, then 2,200 halved between the two.
        result["home-assistant"].ShouldBe(3200m);
        result["home-watches"].ShouldBe(1100m);
        result.Values.Sum().ShouldBe(4300m);
    }

    [Fact]
    public async Task Grouped_LatencyByAgent_AggregatesOverTheJudgmentsThatHaveOne()
    {
        var avg = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Agent, SkillPreloadMetric.LatencyMs, _date, _date);
        var max = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Agent, SkillPreloadMetric.LatencyMs, _date, _date, Aggregation.Max);

        avg["nabu"].ShouldBe(400m);
        avg["jonas"].ShouldBe(400m);
        max["jonas"].ShouldBe(600m);
    }

    [Fact]
    public async Task Grouped_InputTokensByChannel_SumsThem()
    {
        var result = await _sut.GetSkillPreloadGroupedAsync(SkillPreloadDimension.Channel, SkillPreloadMetric.InputTokens, _date, _date);

        result["voice"].ShouldBe(4300m);
        result["signalr"].ShouldBe(2000m);
    }

    [Fact]
    public async Task Trend_IsOneSeriesPerOutcome_CountedPerBucket()
    {
        var trend = await _sut.GetSkillPreloadTrendAsync(_date, _date);

        trend.Select(s => s.Stage).ShouldBe(["deadline", "none", "preloaded", "skipped-lemonade"]);
        var preloaded = trend.Single(s => s.Stage == SkillPreloadOutcomes.Preloaded);
        preloaded.Points.Single(p => p.Bucket.Hour == 10).Value.ShouldBe(2m);
        trend.Single(s => s.Stage == SkillPreloadOutcomes.Deadline)
            .Points.Single(p => p.Value > 0).Bucket.Hour.ShouldBe(11);
    }

    // A count series is drawn as a line, so a bucket a series has no events in is a zero, not an
    // absence: without the zero the line goes straight from one occurrence to the next and reads
    // as a steady rate over every hour between them. Latency is the opposite — no data is no
    // point — which is why this is filled here and not there.
    [Fact]
    public async Task Trend_ABucketAnOutcomeIsAbsentFrom_IsAZero_NotAGap()
    {
        var trend = await _sut.GetSkillPreloadTrendAsync(_date, _date);

        var preloaded = trend.Single(s => s.Stage == SkillPreloadOutcomes.Preloaded);
        preloaded.Points.Select(p => p.Bucket.Hour).ShouldBe([10, 11]);
        preloaded.Points.Single(p => p.Bucket.Hour == 11).Value.ShouldBe(0m);

        var deadline = trend.Single(s => s.Stage == SkillPreloadOutcomes.Deadline);
        deadline.Points.Single(p => p.Bucket.Hour == 10).Value.ShouldBe(0m);
    }
}