using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

// Store and search semantics driven by real embeddings from the local Lemonade server:
// category filtering, per-user isolation and importance weighting all depend on genuine
// vectors of the configured width, which a fabricated array cannot provide.
[Trait("Category", "External")]
[Collection(LemonadeCollection.Name)]
public class MemoryStoreWithEmbeddingsTests(
    LemonadeMemorySearchFixture redisFixture, LemonadeFixture lemonadeFixture)
    : IClassFixture<LemonadeMemorySearchFixture>
{
    private IEmbeddingService CreateEmbeddingService()
    {
        return new EmbeddingService(new HttpClient(), new EmbeddingOptions
        {
            BaseAddress = lemonadeFixture.BaseUrl,
            Model = LemonadeFixture.EmbeddingModel,
            Dimension = LemonadeFixture.EmbeddingDimension,
            Timeout = TimeSpan.FromMinutes(2)
        });
    }

    private RedisStackMemoryStore CreateStore()
    {
        return new RedisStackMemoryStore(
            redisFixture.Connection, TestEmbeddingOptions.At(LemonadeFixture.EmbeddingDimension));
    }

    private async Task<MemoryEntry> CreateMemoryWithEmbedding(
        IEmbeddingService embeddingService,
        string userId,
        string content,
        MemoryCategory category = MemoryCategory.Fact)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(content);
        return new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = category,
            Content = content,
            Importance = 0.7,
            Confidence = 0.8,
            Embedding = embedding,
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };
    }

    [SkippableFact]
    public async Task FullFlow_StoreAndRetrieveWithSemanticSearch()
    {
        Skip.If(lemonadeFixture.SkipReason is not null, lemonadeFixture.SkipReason);

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var userId = $"user_{Guid.NewGuid():N}";

        var memory1 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User is a senior backend developer specializing in Go and microservices");
        var memory2 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User prefers vim keybindings in all editors");
        var memory3 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User is working on a distributed cache system");
        var memory4 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User likes coffee, especially dark roast");

        await store.StoreAsync(memory1);
        await store.StoreAsync(memory2);
        await store.StoreAsync(memory3);
        await store.StoreAsync(memory4);

        // Act - Search for programming-related memories
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(
            "What programming languages and systems does the user work with?");

        var results = await store.SearchAsync(userId, queryEmbedding: queryEmbedding, limit: 2);

        // Assert
        results.Count.ShouldBe(2);

        var contents = results.Select(r => r.Memory.Content).ToList();
        contents.ShouldNotContain(c => c.Contains("coffee"));

        contents.Any(c => c.Contains("backend") || c.Contains("microservices") || c.Contains("distributed"))
            .ShouldBeTrue();
    }

    [SkippableFact]
    public async Task FullFlow_CategoryFilterWithSemanticRanking()
    {
        Skip.If(lemonadeFixture.SkipReason is not null, lemonadeFixture.SkipReason);

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var userId = $"user_{Guid.NewGuid():N}";

        var pref1 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User prefers TypeScript over JavaScript", MemoryCategory.Preference);
        var pref2 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User likes concise code with minimal comments", MemoryCategory.Preference);
        var skill1 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User is proficient in TypeScript and React", MemoryCategory.Skill);

        await store.StoreAsync(pref1);
        await store.StoreAsync(pref2);
        await store.StoreAsync(skill1);

        // Act - Search preferences only for TypeScript-related query
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync("TypeScript coding style");

        var results = await store.SearchAsync(
            userId,
            queryEmbedding: queryEmbedding,
            categories: [MemoryCategory.Preference],
            limit: 5);

        // Assert
        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.Memory.Category == MemoryCategory.Preference);

        results[0].Memory.Content.ShouldContain("TypeScript");
    }

    [SkippableFact]
    public async Task FullFlow_UserIsolation_SemanticSearchRespectsBoundaries()
    {
        Skip.If(lemonadeFixture.SkipReason is not null, lemonadeFixture.SkipReason);

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var user1 = $"user_{Guid.NewGuid():N}";
        var user2 = $"user_{Guid.NewGuid():N}";

        // Both users have Python-related memories
        var user1Memory = await CreateMemoryWithEmbedding(embeddingService, user1,
            "User 1 is a Python expert with 10 years experience");
        var user2Memory = await CreateMemoryWithEmbedding(embeddingService, user2,
            "User 2 is learning Python as their first language");

        await store.StoreAsync(user1Memory);
        await store.StoreAsync(user2Memory);

        // Act - Search for Python in user1's context
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync("Python programming experience");
        var user1Results = await store.SearchAsync(user1, queryEmbedding: queryEmbedding);
        var user2Results = await store.SearchAsync(user2, queryEmbedding: queryEmbedding);

        // Assert - Each user only sees their own memories
        user1Results.Count.ShouldBe(1);
        user1Results[0].Memory.Content.ShouldContain("User 1");

        user2Results.Count.ShouldBe(1);
        user2Results[0].Memory.Content.ShouldContain("User 2");
    }

    [SkippableFact]
    public async Task FullFlow_ImportanceWeighting_AffectsRanking()
    {
        Skip.If(lemonadeFixture.SkipReason is not null, lemonadeFixture.SkipReason);

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var userId = $"user_{Guid.NewGuid():N}";

        var embedding = await embeddingService.GenerateEmbeddingAsync("User works with databases");

        var lowImportance = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = MemoryCategory.Skill,
            Content = "User mentioned databases once",
            Importance = 0.2,
            Confidence = 0.5,
            Embedding = embedding,
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        var highImportance = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = MemoryCategory.Skill,
            Content = "User explicitly stated expertise in PostgreSQL databases",
            Importance = 0.95,
            Confidence = 0.9,
            Embedding = embedding, // Same embedding for fair comparison
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        await store.StoreAsync(lowImportance);
        await store.StoreAsync(highImportance);

        // Act
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync("database skills");
        var results = await store.SearchAsync(userId, queryEmbedding: queryEmbedding);

        // Assert - High importance should rank first due to weighted relevance
        results.Count.ShouldBe(2);
        results[0].Memory.Importance.ShouldBeGreaterThan(results[1].Memory.Importance);
        results[0].Memory.Content.ShouldContain("PostgreSQL");
    }
}