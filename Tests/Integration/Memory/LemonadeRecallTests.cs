using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
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

// Drives recall end to end against a real Redis and a real Lemonade: a genuine 1024-wide
// vector makes the round trip through the index, so the embedding swap, the index width and
// the search are proven together rather than one at a time against mocks.
[Trait("Category", "External")]
[Collection(LemonadeCollection.Name)]
public class LemonadeRecallTests(LemonadeMemorySearchFixture redisFixture, LemonadeFixture lemonadeFixture)
    : IClassFixture<LemonadeMemorySearchFixture>
{
    [SkippableFact]
    public async Task AUserWithMemories_GetsThemBackAtTheLocalDimension()
    {
        Skip.If(lemonadeFixture.SkipReason is not null, lemonadeFixture.SkipReason);

        var embeddingService = CreateEmbeddingService();
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";

        await StoreAsync(store, embeddingService, userId, "Al usuario le gusta TypeScript más que JavaScript");
        await StoreAsync(store, embeddingService, userId, "Al usuario le gusta el café tostado oscuro");

        var message = new ChatMessage(ChatRole.User, "¿Qué lenguaje debería usar?");
        await CreateHook(store, embeddingService)
            .EnrichAsync(message, userId, "conv_1", null, CreateSession(), CancellationToken.None);

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Memories.ShouldContain(m => m.Memory.Content.Contains("TypeScript"));
    }

    [SkippableFact]
    public async Task AnOutage_ProducesATurnWithNoRecallBlockRatherThanAFailedTurn()
    {
        Skip.If(lemonadeFixture.SkipReason is not null, lemonadeFixture.SkipReason);

        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await StoreAsync(store, CreateEmbeddingService(), userId, "Al usuario le gusta TypeScript");

        // Nothing is listening on this port, which is what a stopped Lemonade looks like.
        var unreachable = new EmbeddingService(new HttpClient(), new EmbeddingOptions
        {
            BaseAddress = "http://127.0.0.1:9/v1/",
            Model = LemonadeFixture.EmbeddingModel,
            Dimension = LemonadeFixture.EmbeddingDimension,
            Timeout = TimeSpan.FromSeconds(5)
        });

        var errors = new List<ErrorEvent>();
        var metricsPublisher = new Mock<IMetricsPublisher>();
        metricsPublisher.Setup(p => p.Publish(It.IsAny<ErrorEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is ErrorEvent error)
                {
                    errors.Add(error);
                }
            });

        var message = new ChatMessage(ChatRole.User, "¿Qué lenguaje debería usar?");
        await CreateHook(store, unreachable, metricsPublisher.Object)
            .EnrichAsync(message, userId, "conv_1", null, CreateSession(), CancellationToken.None);

        message.GetMemoryContext().ShouldBeNull();
        errors.ShouldContain(e => e.Service == MemoryRecallHook.EmbeddingErrorService);
    }

    private EmbeddingService CreateEmbeddingService() => new(new HttpClient(), new EmbeddingOptions
    {
        BaseAddress = lemonadeFixture.BaseUrl,
        Model = LemonadeFixture.EmbeddingModel,
        Dimension = LemonadeFixture.EmbeddingDimension,
        Timeout = TimeSpan.FromMinutes(2)
    });

    private RedisStackMemoryStore CreateStore() => new(
        redisFixture.Connection, TestEmbeddingOptions.At(LemonadeFixture.EmbeddingDimension));

    private static async Task StoreAsync(
        IMemoryStore store, IEmbeddingService embeddingService, string userId, string content)
    {
        await store.StoreAsync(new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = MemoryCategory.Preference,
            Content = content,
            Importance = 0.9,
            Confidence = 0.8,
            Embedding = await embeddingService.GenerateEmbeddingAsync(content),
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        });
    }

    private static MemoryRecallHook CreateHook(
        IMemoryStore store, IEmbeddingService embeddingService, IMetricsPublisher? metricsPublisher = null)
    {
        var threadStateStore = new Mock<IThreadStateStore>();
        threadStateStore.Setup(s => s.GetMessageCountAsync(It.IsAny<string>())).ReturnsAsync(0L);
        threadStateStore.Setup(s => s.GetTailMessagesAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        return new MemoryRecallHook(
            store,
            embeddingService,
            threadStateStore.Object,
            new MemoryExtractionQueue(),
            metricsPublisher ?? Mock.Of<IMetricsPublisher>(),
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
}