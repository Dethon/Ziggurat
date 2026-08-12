using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Memory;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

[Trait("Category", "Integration")]
public class MemoryRecallHookIntegrationTests(MemorySearchFixture redisFixture)
    : IClassFixture<MemorySearchFixture>
{
    [Fact]
    public async Task EnrichAsync_WithStoredMemories_InjectsContextIntoMessage()
    {
        var store = new RedisStackMemoryStore(redisFixture.Connection, TestEmbeddingOptions.At(MemorySearchFixture.VectorWidth));
        var embeddingService = new Mock<IEmbeddingService>();
        var queue = new MemoryExtractionQueue();
        var metricsPublisher = new Mock<IMetricsPublisher>();

        var userId = $"user_{Guid.NewGuid():N}";
        var embedding = CreateTestEmbedding();

        await store.StoreAsync(new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = MemoryCategory.Preference,
            Content = "User prefers TypeScript over JavaScript",
            Importance = 0.9,
            Confidence = 0.8,
            Embedding = embedding,
            Tags = ["language"],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        });

        embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        var agentDefinitionProvider = new Mock<IAgentDefinitionProvider>();
        var threadStateStore = new Mock<IThreadStateStore>();
        threadStateStore.Setup(s => s.GetMessageCountAsync(It.IsAny<string>()))
            .ReturnsAsync(0L);
        threadStateStore.Setup(s => s.GetTailMessagesAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        var hook = new MemoryRecallHook(
            store, embeddingService.Object, threadStateStore.Object, queue, metricsPublisher.Object,
            agentDefinitionProvider.Object,
            Mock.Of<ILogger<MemoryRecallHook>>(),
            new MemoryRecallOptions());

        var message = new ChatMessage(ChatRole.User, "What language should I use?");

        var session = new Mock<AgentSession>().Object;
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, "test-state-key");

        await hook.EnrichAsync(message, userId, "conv_1", null, session, CancellationToken.None);

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Memories.Count.ShouldBeGreaterThan(0);
        context.Memories.ShouldContain(m => m.Memory.Content.Contains("TypeScript"));
    }

    [Fact]
    public async Task EnrichAsync_UnrememberedUser_SkipsEmbeddingAndSearchButKeepsProfileAndExtraction()
    {
        var store = new RedisStackMemoryStore(redisFixture.Connection, TestEmbeddingOptions.At(MemorySearchFixture.VectorWidth));
        var embeddingService = new Mock<IEmbeddingService>();
        var queue = new MemoryExtractionQueue();

        var userId = $"user_{Guid.NewGuid():N}";
        await store.SaveProfileAsync(new PersonalityProfile
        {
            UserId = userId, Summary = "Habla claro y va al grano", LastUpdated = DateTimeOffset.UtcNow
        });

        var message = new ChatMessage(ChatRole.User, "¿Qué lenguaje debería usar?");
        await CreateHook(store, embeddingService.Object, queue)
            .EnrichAsync(message, userId, "conv_1", null, CreateSession(), CancellationToken.None);

        embeddingService.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Profile.ShouldNotBeNull();
        context.Memories.ShouldBeEmpty();

        queue.Complete();
        var request = await queue.ReadAllAsync(CancellationToken.None).FirstAsync();
        request.UserId.ShouldBe(userId);
    }

    [Fact]
    public async Task EnrichAsync_AfterTheirFirstMemoryLands_SearchesOnTheVeryNextTurn()
    {
        var store = new RedisStackMemoryStore(redisFixture.Connection, TestEmbeddingOptions.At(MemorySearchFixture.VectorWidth));
        var embeddingService = new Mock<IEmbeddingService>();
        embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestEmbedding());

        var userId = $"user_{Guid.NewGuid():N}";
        var hook = CreateHook(store, embeddingService.Object, new MemoryExtractionQueue());

        await hook.EnrichAsync(new ChatMessage(ChatRole.User, "primera"), userId, "c", null,
            CreateSession(), CancellationToken.None);
        embeddingService.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await store.StoreAsync(new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = MemoryCategory.Preference,
            Content = "Al usuario le gusta TypeScript",
            Importance = 0.9,
            Confidence = 0.8,
            Embedding = CreateTestEmbedding(),
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        });

        await hook.EnrichAsync(new ChatMessage(ChatRole.User, "segunda"), userId, "c", null,
            CreateSession(), CancellationToken.None);
        embeddingService.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MemoryRecallHook CreateHook(
        IMemoryStore store, IEmbeddingService embeddingService, MemoryExtractionQueue queue)
    {
        var threadStateStore = new Mock<IThreadStateStore>();
        threadStateStore.Setup(s => s.GetMessageCountAsync(It.IsAny<string>())).ReturnsAsync(0L);
        threadStateStore.Setup(s => s.GetTailMessagesAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        return new MemoryRecallHook(
            store, embeddingService, threadStateStore.Object, queue,
            new Mock<IMetricsPublisher>().Object,
            new Mock<IAgentDefinitionProvider>().Object,
            Mock.Of<ILogger<MemoryRecallHook>>(),
            new MemoryRecallOptions());
    }

    private static AgentSession CreateSession()
    {
        var session = new Mock<AgentSession>().Object;
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, $"state-{Guid.NewGuid():N}");
        return session;
    }

    private static float[] CreateTestEmbedding()
    {
        var rng = new Random(42);
        return Enumerable.Range(0, 1536).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
    }
}