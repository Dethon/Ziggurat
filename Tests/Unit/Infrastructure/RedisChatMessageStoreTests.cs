#pragma warning disable MAAI001 // ChatHistoryProvider.InvokingContext/InvokedContext are evaluation-only APIs
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class RedisChatMessageStoreTests
{
    [Fact]
    public async Task InvokingAsync_WithStringKey_UsesKeyDirectly()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        var agentKey = new AgentKey("123:456");
        var session = CreateSessionWithKey(agentKey.ToString());
        var store = new RedisChatMessageStore(mockStore.Object);

        // Act
        await store.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(new Mock<AIAgent>().Object, session, []),
            CancellationToken.None);

        // Assert
        mockStore.Verify(s => s.GetMessagesAsync(agentKey.ToString()), Times.Once);
    }

    [Fact]
    public async Task InvokingAsync_WithNoState_UsesGuidKey()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        var session = new Mock<AgentSession>().Object;
        var store = new RedisChatMessageStore(mockStore.Object);

        // Act
        await store.InvokingAsync(
            new ChatHistoryProvider.InvokingContext(new Mock<AIAgent>().Object, session, []),
            CancellationToken.None);

        // Assert
        mockStore.Verify(s => s.GetMessagesAsync(It.Is<string>(k => IsGuid(k))), Times.Once);
    }

    private static bool IsGuid(string value)
    {
        return Guid.TryParse(value, out _);
    }

    [Fact]
    public async Task InvokedAsync_ResponseMessages_HaveTimestampAfterStorage()
    {
        // Arrange
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);

        IReadOnlyList<ChatMessage>? savedMessages = null;
        mockStore.Setup(s => s.AppendMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Callback<string, IReadOnlyList<ChatMessage>>((_, msgs) => savedMessages = msgs)
            .Returns(Task.CompletedTask);

        var session = CreateSessionWithKey("test-key");
        var store = new RedisChatMessageStore(mockStore.Object);

        // Simulate what the framework produces: response messages without timestamps
        // (ToChatResponse drops AdditionalProperties from streaming updates)
        var responseMessage = new ChatMessage(ChatRole.Assistant, "Hello from agent");

        var invokedContext = new ChatHistoryProvider.InvokedContext(
            new Mock<AIAgent>().Object,
            session,
            [new ChatMessage(ChatRole.User, "Hi")],
            [responseMessage]);

        // Act
        await store.InvokedAsync(invokedContext, CancellationToken.None);

        // Assert
        savedMessages.ShouldNotBeNull();
        savedMessages.Select(m => m.Text).ShouldBe(["Hi", "Hello from agent"]);
        var assistantMsg = savedMessages.First(m => m.Role == ChatRole.Assistant);
        assistantMsg.GetTimestamp().ShouldNotBeNull();
        // Append model: persistence must not re-read history (no O(n) get-concat-set).
        mockStore.Verify(s => s.GetMessagesAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void TryGetStateKey_WhenStateBagContainsEmptyString_ReturnsFalseAndNull()
    {
        var session = CreateSessionWithKey(string.Empty);

        var result = RedisChatMessageStore.TryGetStateKey(session, out var stateKey);

        result.ShouldBeFalse();
        stateKey.ShouldBeNull();
    }

    [Fact]
    public async Task StoreChatHistoryAsync_PublishesHistoryStoreLatencyEvent()
    {
        // Arrange - mirror InvokedAsync_ResponseMessages_HaveTimestampAfterStorage exactly,
        // but also wire in a publisher and capture the emitted event.
        var mockStore = new Mock<IThreadStateStore>();
        mockStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>())).ReturnsAsync((ChatMessage[]?)null);
        mockStore.Setup(s => s.AppendMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Returns(Task.CompletedTask);

        var publisher = new Mock<IMetricsPublisher>();
        LatencyEvent? captured = null;
        publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e => { if (e is LatencyEvent l) { captured = l; } });

        var session = CreateSessionWithKey("test-key");
        var store = new RedisChatMessageStore(mockStore.Object, publisher.Object, "conv1");

        var invokedContext = new ChatHistoryProvider.InvokedContext(
            new Mock<AIAgent>().Object,
            session,
            [new ChatMessage(ChatRole.User, "Hi")],
            [new ChatMessage(ChatRole.Assistant, "Hello")]);

        // Act
        await store.InvokedAsync(invokedContext, CancellationToken.None);

        // Assert
        captured.ShouldNotBeNull();
        captured.Stage.ShouldBe(LatencyStage.HistoryStore);
        captured.ConversationId.ShouldBe("conv1");
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    private static AgentSession CreateSessionWithKey(string key)
    {
        var session = new Mock<AgentSession>().Object;
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, key);
        return session;
    }
}