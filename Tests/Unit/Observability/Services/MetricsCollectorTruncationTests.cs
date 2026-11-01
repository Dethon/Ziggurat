using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Observability.Hubs;
using Observability.Services;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsCollectorTruncationTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly Mock<IHubContext<MetricsHub>> _hubContext = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly MetricsCollectorService _sut;

    public MetricsCollectorTruncationTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _hubClients.Setup(c => c.All).Returns(_clientProxy.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _sut = new MetricsCollectorService(
            _redis.Object, _hubContext.Object,
            NullLogger<MetricsCollectorService>.Instance);
    }

    [Fact]
    public async Task ProcessEventAsync_ContextTruncation_AddsToTimelineAndBroadcasts()
    {
        var evt = new ContextTruncationEvent
        {
            Sender = "alice",
            Model = "z-ai/glm-5.1",
            DroppedMessages = 3,
            EstimatedTokensBefore = 500,
            EstimatedTokensAfter = 350,
            MaxContextTokens = 400,
            Timestamp = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:truncations:deployment:2026-05-03",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()),
            Times.Never);
        _clientProxy.Verify(p => p.SendCoreAsync(
            "OnContextTruncation",
            It.Is<object?[]>(args => args.Length == 1 && Equals(args[0], evt)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}