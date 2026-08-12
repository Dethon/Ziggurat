using Domain.DTOs;
using Infrastructure.Memory;

namespace Tests.Integration.Fixtures;

// The Redis a memory-store test needs. RediSearch indexes database 0 and refuses every other one,
// so these classes cannot be separated by database the way RedisFixture separates the rest — they
// share database 0 and are kept apart by pool. The store names its index with a constant, and an
// index is created at one vector width, so a pool holds exactly one width.
//
// Tests take the width from the fixture rather than a constant of their own: on a shared index it
// is the pool's property, not the test's.
public abstract class RedisSearchFixture(int vectorDimension) : RedisFixture
{
    public int VectorDimension { get; } = vectorDimension;

    protected override async Task<RedisLease> AcquireAsync()
    {
        var pool = await RedisPool.GetAsync($"search-{VectorDimension}", CreateIndexAsync);
        return pool.ShareSearchDatabase();
    }

    // The store creates its index on first use, and classes on this pool run in parallel: two of
    // them missing it at once would race, and the loser's FT.CREATE fails with "Index already
    // exists" out of whatever call it was making. Doing it once while the pool starts is the same
    // production code path, just not from three threads.
    private async Task CreateIndexAsync(RedisPool pool)
    {
        await using var connection = await pool.ConnectAsync(0);
        var store = new RedisStackMemoryStore(connection, TestEmbeddingOptions.At(VectorDimension));

        var probe = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = $"index-warmup_{Guid.NewGuid():N}",
            Category = MemoryCategory.Fact,
            Content = "Creates the index the suite shares",
            Importance = 0.1,
            Confidence = 0.1,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        await store.StoreAsync(probe);
        await store.DeleteAsync(probe.UserId, probe.Id);
    }
}

// Fabricated vectors. Deliberately not the configured production width: the store builds its index
// at whatever dimension it is given, and these tests pin that it follows the configuration rather
// than a constant of its own.
public sealed class MemorySearchFixture() : RedisSearchFixture(VectorWidth)
{
    public const int VectorWidth = 1536;
}

// Real embeddings from the local Lemonade server, which produces vectors of its own width.
public sealed class LemonadeMemorySearchFixture() : RedisSearchFixture(LemonadeFixture.EmbeddingDimension);