using Moq;
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

    [Fact]
    public async Task GetSummaryAsync_MultipleDays_SumsAcrossDays()
    {
        var from = new DateOnly(2026, 3, 14);
        var to = new DateOnly(2026, 3, 15);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-14", It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                new HashEntry("tokens:input", 100),
                new HashEntry("tokens:output", 50),
                new HashEntry("tokens:cost", 100),
                new HashEntry("tools:count", 10),
                new HashEntry("tools:errors", 1)
            ]);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                new HashEntry("tokens:input", 200),
                new HashEntry("tokens:output", 100),
                new HashEntry("tokens:cost", 200),
                new HashEntry("tools:count", 20),
                new HashEntry("tools:errors", 2)
            ]);

        var result = await _sut.GetSummaryAsync(from, to);

        result.InputTokens.ShouldBe(300);
        result.OutputTokens.ShouldBe(150);
        result.TotalTokens.ShouldBe(450);
        result.Cost.ShouldBe(0.03m);
        result.ToolCalls.ShouldBe(30);
        result.ToolErrors.ShouldBe(3);
    }

    [Fact]
    public async Task GetSummaryAsync_NoData_ReturnsZeros()
    {
        var date = new DateOnly(2026, 3, 15);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        var result = await _sut.GetSummaryAsync(date, date);

        result.InputTokens.ShouldBe(0);
        result.OutputTokens.ShouldBe(0);
        result.TotalTokens.ShouldBe(0);
        result.Cost.ShouldBe(0m);
        result.ToolCalls.ShouldBe(0);
        result.ToolErrors.ShouldBe(0);
    }

    [Fact]
    public async Task GetSummaryAsync_IgnoresUnrelatedHashFields()
    {
        var date = new DateOnly(2026, 3, 15);

        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync([
                new HashEntry("tokens:input", 100),
                new HashEntry("tokens:byUser:alice", 100),
                new HashEntry("tokens:byModel:gpt-4", 100),
                new HashEntry("tools:byName:search", 5)
            ]);

        var result = await _sut.GetSummaryAsync(date, date);

        result.InputTokens.ShouldBe(100);
        result.OutputTokens.ShouldBe(0);
        result.ToolCalls.ShouldBe(0);
    }

    [Fact]
    public async Task GetSummaryAsync_WithMemoryFields_ReturnsMemoryTotals()
    {
        var date = new DateOnly(2026, 3, 15);
        _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
            .ReturnsAsync(
            [
                new HashEntry("tokens:input", 100),
                new HashEntry("tokens:output", 50),
                new HashEntry("tokens:cost", 10000),
                new HashEntry("tools:count", 10),
                new HashEntry("tools:errors", 2),
                new HashEntry("memory:recalls", 25),
                new HashEntry("memory:extractions", 15),
                new HashEntry("memory:dreamings", 3),
                new HashEntry("memory:stored", 42),
                new HashEntry("memory:merged", 7),
                new HashEntry("memory:decayed", 4),
            ]);

        var result = await _sut.GetSummaryAsync(date, date);

        result.TotalRecalls.ShouldBe(25);
        result.TotalExtractions.ShouldBe(15);
        result.TotalDreamings.ShouldBe(3);
        result.MemoriesStored.ShouldBe(42);
        result.MemoriesMerged.ShouldBe(7);
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