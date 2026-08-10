using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Moq;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Metrics;

public class RedisMetricSinkTests
{
    private readonly Mock<ISubscriber> _subscriber = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly RedisMetricSink _sut;

    public RedisMetricSinkTests()
    {
        _redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_subscriber.Object);
        _sut = new RedisMetricSink(_redis.Object);
    }

    [Fact]
    public async Task SendAsync_serializes_heartbeat_event_to_metrics_channel()
    {
        await _sut.SendAsync(new HeartbeatEvent { Service = "agent" });

        VerifyPublished("\"service\":\"agent\"");
    }

    [Fact]
    public async Task SendAsync_serializes_token_usage_event_to_metrics_channel()
    {
        await _sut.SendAsync(new TokenUsageEvent
        {
            Sender = "user1",
            Model = "gpt-4",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.01m
        });

        VerifyPublished("\"type\":\"token_usage\"");
    }

    private void VerifyPublished(string expectedFragment) =>
        _subscriber.Verify(s => s.PublishAsync(
            RedisChannel.Literal("metrics:events"),
            It.Is<RedisValue>(v => v.ToString().Contains(expectedFragment)),
            It.IsAny<CommandFlags>()), Times.Once);
}