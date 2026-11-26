using System.Text.Json;
using Domain.DTOs.Metrics;
using Moq;
using Observability;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly MetricsQueryService _sut;

    public MetricsQueryServiceTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _sut = new MetricsQueryService(_redis.Object);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private void SetupSortedSet(string key, params MetricEvent[] events) =>
        _db.Setup(d => d.SortedSetRangeByScoreAsync(key, It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(events.Select(e => new RedisValue(JsonSerializer.Serialize(e, _jsonOptions))).ToArray());

    private static TokenUsageEvent Call(decimal cost, DateTimeOffset at, int input = 10, int output = 5) =>
        new() { Sender = "ana", Model = "m", InputTokens = input, OutputTokens = output, Cost = cost, Timestamp = at };

    private static ToolCallEvent Tool(bool success, DateTimeOffset at) =>
        new() { ToolName = "search", DurationMs = 5, Success = success, Timestamp = at };

    [Fact]
    public async Task GetSummaryAsync_MultipleDays_SumsAcrossDays()
    {
        var day1 = new DateTimeOffset(2026, 3, 14, 9, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        SetupSortedSet("metrics:tokens:deployment:2026-03-14", Call(0.01m, day1, 100, 50));
        SetupSortedSet("metrics:tokens:deployment:2026-03-15", Call(0.02m, day2, 200, 100));
        SetupSortedSet("metrics:tools:deployment:2026-03-14", Tool(true, day1), Tool(false, day1));
        SetupSortedSet("metrics:tools:deployment:2026-03-15", Tool(true, day2));

        var result = await _sut.GetSummaryAsync(
            MetricDateRange.Spanning(new DateOnly(2026, 3, 14), new DateOnly(2026, 3, 15)));

        result.InputTokens.ShouldBe(300);
        result.OutputTokens.ShouldBe(150);
        result.TotalTokens.ShouldBe(450);
        result.Cost.ShouldBe(0.03m);
        result.ToolCalls.ShouldBe(3);
        result.ToolErrors.ShouldBe(1);
    }

    // The cost is the calls' own, to the last digit they carry. Most calls cost less than a
    // hundredth of a cent, and the total kept as whole ten-thousandths of a dollar dropped that
    // remainder from every one of them: about four percent of a day's spend, on the Overview only.
    [Fact]
    public async Task GetSummaryAsync_CallsCheaperThanATenThousandthOfADollar_AllCount()
    {
        var at = new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);
        SetupSortedSet("metrics:tokens:deployment:2026-03-15",
            Call(0.00003m, at), Call(0.00009m, at), Call(0.1234567m, at));

        var result = await _sut.GetSummaryAsync(MetricDateRange.Spanning(new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 15)));

        result.Cost.ShouldBe(0.1235767m);
    }

    [Fact]
    public async Task GetSummaryAsync_NoData_ReturnsZeros()
    {
        var result = await _sut.GetSummaryAsync(MetricDateRange.Spanning(new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 15)));

        result.InputTokens.ShouldBe(0);
        result.OutputTokens.ShouldBe(0);
        result.TotalTokens.ShouldBe(0);
        result.Cost.ShouldBe(0m);
        result.ToolCalls.ShouldBe(0);
        result.ToolErrors.ShouldBe(0);
    }

    [Fact]
    public async Task GetSummaryAsync_WithMemoryEvents_ReturnsMemoryTotals()
    {
        var at = new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);
        SetupSortedSet("metrics:memory-recall:deployment:2026-03-15",
            new MemoryRecallEvent { DurationMs = 1, MemoryCount = 3, UserId = "ana", Timestamp = at },
            new MemoryRecallEvent { DurationMs = 1, MemoryCount = 0, UserId = "ana", Timestamp = at });
        SetupSortedSet("metrics:memory-extraction:deployment:2026-03-15",
            new MemoryExtractionEvent { DurationMs = 1, CandidateCount = 5, StoredCount = 4, UserId = "ana", Timestamp = at });
        SetupSortedSet("metrics:memory-dreaming:deployment:2026-03-15",
            new MemoryDreamingEvent { MergedCount = 7, DecayedCount = 2, ProfileRegenerated = false, UserId = "ana", Timestamp = at },
            new MemoryDreamingEvent { MergedCount = 1, DecayedCount = 2, ProfileRegenerated = false, UserId = "bo", Timestamp = at });

        var result = await _sut.GetSummaryAsync(MetricDateRange.Spanning(new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 15)));

        result.TotalRecalls.ShouldBe(2);
        result.TotalExtractions.ShouldBe(1);
        result.TotalDreamings.ShouldBe(2);
        result.MemoriesStored.ShouldBe(4);
        result.MemoriesMerged.ShouldBe(8);
        result.MemoriesDecayed.ShouldBe(4);
    }

    [Fact]
    public async Task GetHealthAsync_ReadsTheRosterFromTheSeenSortedSet()
    {
        _db.Setup(d => d.SortedSetRangeByRankAsync(
                "metrics:health:seen", 0, -1, Order.Ascending, CommandFlags.None))
            .ReturnsAsync([(RedisValue)"lemonade", (RedisValue)"tse-extractor"]);
        _db.Setup(d => d.StringGetAsync("metrics:health:lemonade", It.IsAny<CommandFlags>()))
            .ReturnsAsync("2026-03-15T10:00:00+00:00");
        _db.Setup(d => d.StringGetAsync("metrics:health:tse-extractor", It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var result = await _sut.GetHealthAsync();

        result.Select(r => r.Service).ShouldBe(["lemonade", "tse-extractor"], ignoreOrder: true);
        result.Single(r => r.Service == "lemonade").IsHealthy.ShouldBeTrue();
        result.Single(r => r.Service == "tse-extractor").IsHealthy.ShouldBeFalse();
        _db.Invocations.ShouldNotContain(i => i.Method.Name == "SetMembersAsync");
    }
}