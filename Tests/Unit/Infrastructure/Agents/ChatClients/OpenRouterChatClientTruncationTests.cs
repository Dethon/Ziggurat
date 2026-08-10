using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterChatClientTruncationTests
{
    private readonly Mock<IChatClient> _innerClient = new();
    private readonly Mock<IMetricsPublisher> _publisher = new();

    [Fact]
    public async Task GetStreamingResponseAsync_OverThreshold_DropsAndPublishesEvent()
    {
        var sut = new OpenRouterChatClient(
            _innerClient.Object, "test-model", maxContextTokens: 80,
            metricsPublisher: _publisher.Object);

        var sys = new ChatMessage(ChatRole.System, new string('s', 4));
        var u1 = new ChatMessage(ChatRole.User, new string('a', 400));
        var u2 = new ChatMessage(ChatRole.User, "hi");
        u2.SetSenderId("alice");

        IEnumerable<ChatMessage>? captured = null;
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, _, _) => captured = msgs.ToList())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        ContextTruncationEvent? publishedEvent = null;
        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is ContextTruncationEvent t)
                {
                    publishedEvent = t;
                }
            });

        await foreach (var _ in sut.GetStreamingResponseAsync([sys, u1, u2]))
        { }

        captured!.Count().ShouldBeLessThan(3); // u1 dropped
        publishedEvent.ShouldNotBeNull();
        publishedEvent!.Sender.ShouldBe("alice");
        publishedEvent.Model.ShouldBe("test-model");
        publishedEvent.DroppedMessages.ShouldBeGreaterThanOrEqualTo(1);
        publishedEvent.MaxContextTokens.ShouldBe(80);
        publishedEvent.EstimatedTokensAfter.ShouldBeLessThan(publishedEvent.EstimatedTokensBefore);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_OverheadFromInstructionsTriggersTruncation()
    {
        // Messages alone are tiny, but Instructions push us over the threshold.
        var sut = new OpenRouterChatClient(
            _innerClient.Object, "test-model", maxContextTokens: 80,
            metricsPublisher: _publisher.Object);

        var sys = new ChatMessage(ChatRole.System, "s");
        var u1 = new ChatMessage(ChatRole.User, new string('a', 80)); // 24 tokens
        var u2 = new ChatMessage(ChatRole.User, "hi");
        u2.SetSenderId("alice");

        // 80*4=320 chars → 80 tokens of instructions, dwarfing the 80-token budget.
        var options = new ChatOptions { Instructions = new string('x', 320) };

        IEnumerable<ChatMessage>? captured = null;
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, _, _) => captured = msgs.ToList())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        ContextTruncationEvent? publishedEvent = null;
        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is ContextTruncationEvent t)
                {
                    publishedEvent = t;
                }
            });

        await foreach (var _ in sut.GetStreamingResponseAsync([sys, u1, u2], options))
        { }

        publishedEvent.ShouldNotBeNull();
        publishedEvent!.DroppedMessages.ShouldBeGreaterThanOrEqualTo(1);
        captured!.ShouldNotContain(u1); // dropped to make room for overhead
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UnderThreshold_DoesNotPublishTruncationEvent()
    {
        var sut = new OpenRouterChatClient(
            _innerClient.Object, "test-model", maxContextTokens: 100000,
            metricsPublisher: _publisher.Object);

        var u = new ChatMessage(ChatRole.User, "hi");
        u.SetSenderId("alice");

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        await foreach (var _ in sut.GetStreamingResponseAsync([u]))
        { }

        _publisher.Verify(
            p => p.Publish(It.IsAny<ContextTruncationEvent>()),
            Times.Never);
    }
}