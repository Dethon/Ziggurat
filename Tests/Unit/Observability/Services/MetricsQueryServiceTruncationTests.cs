using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceTruncationTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly MetricsQueryService _sut;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MetricsQueryServiceTruncationTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _sut = new MetricsQueryService(_redis.Object);
    }

    private void SetupTruncationEvents(string key, IEnumerable<ContextTruncationEvent> events)
    {
        var entries = events
            .Select(e => new RedisValue(JsonSerializer.Serialize<MetricEvent>(e, _jsonOptions)))
            .ToArray();
        _db.Setup(d => d.SortedSetRangeByScoreAsync(key, It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(entries);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_TruncationCountByModel_GroupsByModel()
    {
        var date = new DateOnly(2026, 5, 3);
        var ts = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);
        SetupTruncationEvents("metrics:truncations:deployment:2026-05-03",
        [
            new() { Sender = "alice", Model = "m-A", DroppedMessages = 1, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts },
            new() { Sender = "bob",   Model = "m-A", DroppedMessages = 2, EstimatedTokensBefore = 200, EstimatedTokensAfter = 150, MaxContextTokens = 200, Timestamp = ts },
            new() { Sender = "alice", Model = "m-B", DroppedMessages = 4, EstimatedTokensBefore = 400, EstimatedTokensAfter = 300, MaxContextTokens = 400, Timestamp = ts }
        ]);

        var result = await _sut.GetTokenGroupedAsync(
            TokenDimension.Model, TokenMetric.TruncationCount, date, date);

        result["m-A"].ShouldBe(2m);
        result["m-B"].ShouldBe(1m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_MessagesDroppedByUser_SumsDroppedCounts()
    {
        var date = new DateOnly(2026, 5, 3);
        var ts = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);
        SetupTruncationEvents("metrics:truncations:deployment:2026-05-03",
        [
            new() { Sender = "alice", Model = "m", DroppedMessages = 1, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts },
            new() { Sender = "alice", Model = "m", DroppedMessages = 3, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts },
            new() { Sender = "bob",   Model = "m", DroppedMessages = 2, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts }
        ]);

        var result = await _sut.GetTokenGroupedAsync(
            TokenDimension.User, TokenMetric.MessagesDropped, date, date);

        result["alice"].ShouldBe(4m);
        result["bob"].ShouldBe(2m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_TokensTrimmedByModel_SumsBeforeMinusAfter()
    {
        var date = new DateOnly(2026, 5, 3);
        var ts = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);
        SetupTruncationEvents("metrics:truncations:deployment:2026-05-03",
        [
            new() { Sender = "alice", Model = "m-A", DroppedMessages = 1, EstimatedTokensBefore = 500, EstimatedTokensAfter = 400, MaxContextTokens = 500, Timestamp = ts },
            new() { Sender = "bob",   Model = "m-A", DroppedMessages = 1, EstimatedTokensBefore = 200, EstimatedTokensAfter = 100, MaxContextTokens = 200, Timestamp = ts }
        ]);

        var result = await _sut.GetTokenGroupedAsync(
            TokenDimension.Model, TokenMetric.TokensTrimmed, date, date);

        result["m-A"].ShouldBe(200m);
    }
}