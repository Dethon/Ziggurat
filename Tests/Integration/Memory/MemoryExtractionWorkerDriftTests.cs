using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Memory;
using Infrastructure.Memory;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

[Trait("Category", "Integration")]
public class MemoryExtractionWorkerDriftTests(MemorySearchFixture redisFixture)
    : IClassFixture<MemorySearchFixture>
{
    [Fact]
    public async Task ProcessRequestAsync_WhenAdditionalTurnsArrive_DoesNotLeakThemIntoExtraction()
    {
        var stateKey = $"drift-test-{Guid.NewGuid():N}";

        var store = new RedisStackMemoryStore(redisFixture.Connection, TestEmbeddingOptions.At(MemorySearchFixture.VectorWidth));
        var threadStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(5));

        // Arrange: seed initial 3-message thread
        await threadStore.SetMessagesAsync(stateKey,
        [
            new ChatMessage(ChatRole.User, "I'm planning a trip"),
            new ChatMessage(ChatRole.Assistant, "Where to?"),
            new ChatMessage(ChatRole.User, "Japan in April")
        ]);

        // Mock extractor to capture the window it receives
        var extractor = new Mock<IMemoryExtractor>();
        IReadOnlyList<ChatMessage>? capturedWindow = null;
        extractor.Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, string, CancellationToken>((w, _, _) => capturedWindow = w)
            .ReturnsAsync([]);

        var embedding = new Mock<IEmbeddingService>();
        var metrics = new Mock<IMetricsPublisher>();
        var agentDefs = new Mock<IAgentDefinitionProvider>();

        var worker = new MemoryExtractionWorker(
            new MemoryExtractionQueue(),
            extractor.Object,
            embedding.Object,
            store,
            threadStore,
            metrics.Object,
            agentDefs.Object,
            NullLogger<MemoryExtractionWorker>.Instance,
            new MemoryExtractionOptions());

        // Create extraction request anchored at 3 persisted messages; FallbackContent is the current user message
        var request = new MemoryExtractionRequest(
            UserId: $"user-{Guid.NewGuid():N}",
            ThreadStateKey: stateKey,
            Anchor: MemoryAnchor.TakenBeforeCurrentTurnIsPersisted(3),
            ConversationId: "conv-drift",
            AgentId: null)
        {
            FallbackContent = "Japan in April"
        };

        // Act: append 3 more messages to the thread AFTER creating the request
        await threadStore.SetMessagesAsync(stateKey,
        [
            new ChatMessage(ChatRole.User, "I'm planning a trip"),
            new ChatMessage(ChatRole.Assistant, "Where to?"),
            new ChatMessage(ChatRole.User, "Japan in April"),
            new ChatMessage(ChatRole.Assistant, "Great choice!"),
            new ChatMessage(ChatRole.User, "Actually, make it Thailand"),
            new ChatMessage(ChatRole.Assistant, "Thailand is wonderful too")
        ]);

        // Process the request with anchor frozen at index 2
        await worker.ProcessRequestAsync(request, CancellationToken.None);

        // Assert: window contains context from thread[..3] + FallbackContent, not drift messages
        capturedWindow.ShouldNotBeNull();
        capturedWindow.ShouldNotContain(m => m.Text.Contains("Thailand"));
        capturedWindow.ShouldNotContain(m => m.Text == "Great choice!");
        capturedWindow[^1].Text.ShouldBe("Japan in April");
        capturedWindow[^1].Role.ShouldBe(ChatRole.User);
    }
}