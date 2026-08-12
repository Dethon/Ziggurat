using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class McpAgentConversationContextTests
{
    private static readonly ConversationContext _context = new(
        "jack", "conv-42", "fran", new ReplyTarget("signalr", "conv-42"));

    private static (McpAgent Agent, List<ChatOptions?> Captured, IReadOnlyCollection<string> Logs) CreateAgent()
    {
        var captured = new List<ChatOptions?>();
        var logProvider = new CapturingLoggerProvider(LogLevel.Error);
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (_, options, _) => captured.Add(options))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] }
            }.ToAsyncEnumerable());

        var agent = new McpAgent(
            TestAgentSpec.Default with { UserId = "fran", ConversationId = "conv-42" },
            chatClient.Object,
            new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            [],
            LoggerFactory.Create(b => b.AddProvider(logProvider)));

        return (agent, captured, logProvider.Messages);
    }

    [Fact]
    public async Task RunStreaming_WithStampedMessage_CarriesContextInChatOptions()
    {
        var (agent, captured, logs) = CreateAgent();
        await using var _ = agent;

        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConversationContext(_context);

        await agent.RunStreamingAsync([message]).ToListAsync();

        var options = captured.ShouldHaveSingleItem().ShouldNotBeNull();
        options.AdditionalProperties.ShouldNotBeNull();
        options.AdditionalProperties[ConversationContextMeta.OptionsKey].ShouldBe(_context);
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunStreaming_WithoutStampedMessage_SetsAdditionalPropertiesAndLogsError()
    {
        // The key must never be dropped quietly: every tools/call of the 2026-07-28 protocol
        // carries its own conversation context, so an unstamped run is a defect that has to be
        // visible in the logs rather than degrade downstream scoping in silence.
        var (agent, captured, logs) = CreateAgent();
        await using var _ = agent;

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var options = captured.ShouldHaveSingleItem().ShouldNotBeNull();
        options.AdditionalProperties.ShouldNotBeNull();
        logs.ShouldContain(m => m.Contains(ChannelProtocol.ConversationContextMetaKey));
    }
}