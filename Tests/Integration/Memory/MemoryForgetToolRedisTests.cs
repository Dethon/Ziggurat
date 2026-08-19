using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Memory;
using Infrastructure.Memory;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

// The forget tool against the real store, where the search it performs is a k-nearest query with
// no relevance floor: against a small store it returns everything, and "nearest" is not "related".
// What these tests pin is that reaching several memories asks instead of deleting — the eval's
// fake store matches lexically, so its "the fact beside it survived" is not evidence about this.
public class MemoryForgetToolRedisTests(MemorySearchFixture redisFixture)
    : IClassFixture<MemorySearchFixture>
{
    private const int EmbeddingDimension = MemorySearchFixture.VectorWidth;

    private RedisStackMemoryStore CreateStore() =>
        new(redisFixture.Connection, TestEmbeddingOptions.At(EmbeddingDimension));

    private static float[] DirectionAt(int index)
    {
        var embedding = new float[EmbeddingDimension];
        embedding[index % EmbeddingDimension] = 1.0f;
        return embedding;
    }

    private static MemoryEntry CreateMemory(string userId, string content, int direction) => new()
    {
        Id = $"mem_{Guid.NewGuid():N}",
        UserId = userId,
        Category = MemoryCategory.Fact,
        Content = content,
        Importance = 0.6,
        Confidence = 0.8,
        Embedding = DirectionAt(direction),
        Tags = [],
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ForgetByQuery_AgainstAFewMemories_DeletesNothingWithoutConfirmation()
    {
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var flat = CreateMemory(userId, "Busca piso de alquiler en Chamberí", direction: 0);
        var coffee = CreateMemory(userId, "Le gusta el café solo", direction: 1);
        var job = CreateMemory(userId, "Trabaja en Acme como ingeniero de datos", direction: 2);
        await store.StoreAsync(flat);
        await store.StoreAsync(coffee);
        await store.StoreAsync(job);

        // The query embeds right on top of the flat memory; the k-nearest search still returns
        // the other two, because nothing in the store bounds "near".
        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(0)), new FeatureConfig(UserId: userId));
        var result = await tool.Run(query: "lo del piso");

        result["status"]!.GetValue<string>().ShouldBe("confirmation_required");
        result["affectedCount"]!.GetValue<int>().ShouldBe(0);
        (await store.GetByUserIdAsync(userId)).Count.ShouldBe(3);

        // The candidates arrive nearest-first, so the follow-up can name the one that was meant.
        var candidates = result["candidates"]!.AsArray();
        candidates.Count.ShouldBe(3);
        candidates[0]!["id"]!.GetValue<string>().ShouldBe(flat.Id);
    }

    [Fact]
    public async Task ForgetByIds_AfterTheConfirmation_TakesOnlyWhatWasNamed()
    {
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var flat = CreateMemory(userId, "Busca piso de alquiler en Chamberí", direction: 0);
        var coffee = CreateMemory(userId, "Le gusta el café solo", direction: 1);
        await store.StoreAsync(flat);
        await store.StoreAsync(coffee);

        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(0)), new FeatureConfig(UserId: userId));
        var result = await tool.Run(memoryIds: [flat.Id]);

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        var remaining = await store.GetByUserIdAsync(userId);
        remaining.Count.ShouldBe(1);
        remaining[0].Content.ShouldBe("Le gusta el café solo");
    }

    [Fact]
    public async Task ForgetByQuery_ReachingExactlyOne_StillDeletesIt()
    {
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var flat = CreateMemory(userId, "Busca piso de alquiler en Chamberí", direction: 0);
        await store.StoreAsync(flat);

        var tool = new MemoryForgetTool(
            store, new FixedEmbedding(DirectionAt(0)), new FeatureConfig(UserId: userId));
        var result = await tool.Run(query: "lo del piso");

        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
        (await store.GetByUserIdAsync(userId)).ShouldBeEmpty();
    }

    private sealed class FixedEmbedding(float[] vector) : IEmbeddingService
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(vector);

        public Task<float[][]> GenerateEmbeddingsAsync(
            IEnumerable<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(_ => vector).ToArray());
    }
}