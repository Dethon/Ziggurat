using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

[Trait("Category", "Performance")]
public class McpAgentLatencyTests : IAsyncDisposable
{
    private readonly McpAgent _agent;
    private readonly List<LatencyEvent> _events = [];
    private readonly List<TokenUsageEvent> _tokenEvents = [];
    private readonly Lock _lock = new();
    private readonly Mock<IMetricsPublisher> _publisher = new();

    public McpAgentLatencyTests()
    {
        var chatClient = new Mock<IChatClient>();
        var updates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] }
        };
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(updates.ToAsyncEnumerable());

        _publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                lock (_lock)
                {
                    switch (e)
                    {
                        case LatencyEvent le:
                            _events.Add(le);
                            break;
                        case TokenUsageEvent te:
                            _tokenEvents.Add(te);
                            break;
                    }
                }
            });

        var stateStore = new Mock<IThreadStateStore>();
        _agent = new McpAgent(
            TestAgentSpec.Default with { Model = "anthropic/claude", ConversationId = "conv1" },
            chatClient.Object,
            stateStore.Object,
            _publisher.Object,
            TimeProvider.System,
            [],
            []);
    }

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
    }

    private LatencyEvent[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _events];
        }
    }

    [Fact]
    public async Task RunStreaming_EmitsLlmFirstTokenAndLlmTotal_WithModel()
    {
        await _agent.RunStreamingAsync("hello").ToListAsync();

        var events = Snapshot();
        var firstToken = events.FirstOrDefault(e => e.Stage == LatencyStage.LlmFirstToken);
        var total = events.FirstOrDefault(e => e.Stage == LatencyStage.LlmTotal);

        firstToken.ShouldNotBeNull();
        total.ShouldNotBeNull();
        total.Model.ShouldBe("anthropic/claude");
        total.ConversationId.ShouldBe("conv1");
        firstToken.Model.ShouldBe("anthropic/claude");
    }

    [Fact]
    public async Task RunStreaming_WithPatchedModel_EmitsLatencyWithPatchedModel()
    {
        var inner = new Mock<IChatClient>();
        inner
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("hi")] },
                new()
                {
                    Role = ChatRole.Assistant,
                    Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })]
                }
            }.ToAsyncEnumerable());

        // Production wraps the chat client in ToolApprovalChatClient, so the wrapper is part of
        // this test on purpose: the resolved model must reach the request through the decorator
        // chain on the turn's own options.
        using var chatClient = new OpenRouterChatClient(
            inner.Object, "anthropic/claude", metricsPublisher: _publisher.Object);
        using var wrappedClient = new ToolApprovalChatClient(
            chatClient, new Mock<IToolApprovalHandler>().Object, "conv-test");

        await using var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                Model = "anthropic/claude",
                ConversationId = "conv1",
                PatchableModels = new FixedPatchableModelSource(["z-ai/glm"])
            },
            wrappedClient,
            new Mock<IThreadStateStore>().Object,
            _publisher.Object,
            TimeProvider.System,
            [],
            []);

        var message = new ChatMessage(ChatRole.User, "hello");
        message.SetConfigPatch(new AgentConfigPatch { Model = "z-ai/glm" });

        await agent.RunStreamingAsync(message).ToListAsync();

        var events = Snapshot();
        events.First(e => e.Stage == LatencyStage.LlmFirstToken).Model.ShouldBe("z-ai/glm");
        events.First(e => e.Stage == LatencyStage.LlmTotal).Model.ShouldBe("z-ai/glm");

        // Cost and token usage are attributed to the model that produced them, not to the
        // agent's configured model.
        lock (_lock)
        {
            _tokenEvents.ShouldHaveSingleItem().Model.ShouldBe("z-ai/glm");
        }
    }

    [Fact]
    public async Task WarmupSessionAsync_EmitsSessionWarmupLatency()
    {
        var thread = await _agent.CreateSessionAsync();

        await _agent.WarmupSessionAsync(thread);

        var warmup = Snapshot().FirstOrDefault(e => e.Stage == LatencyStage.SessionWarmup);
        warmup.ShouldNotBeNull();
        warmup.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
        warmup.Model.ShouldBeNull();
    }
}