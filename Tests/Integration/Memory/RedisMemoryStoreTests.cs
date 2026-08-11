using Domain.DTOs;
using Infrastructure.Memory;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

public class RedisMemoryStoreTests(MemorySearchFixture redisFixture) : IClassFixture<MemorySearchFixture>
{
    private const int EmbeddingDimension = MemorySearchFixture.VectorWidth;

    private RedisStackMemoryStore CreateStore()
    {
        return new RedisStackMemoryStore(redisFixture.Connection, TestEmbeddingOptions.At(EmbeddingDimension));
    }

    private static float[] CreateTestEmbedding(float primaryValue = 1.0f, int primaryIndex = 0)
    {
        var embedding = new float[EmbeddingDimension];
        embedding[primaryIndex % EmbeddingDimension] = primaryValue;
        // Add some variation to avoid degenerate vectors
        embedding[(primaryIndex + 100) % EmbeddingDimension] = 0.1f;
        return embedding;
    }

    private static MemoryEntry CreateMemory(
        string userId,
        string content,
        MemoryCategory category = MemoryCategory.Fact,
        double importance = 0.5,
        float[]? embedding = null,
        IReadOnlyList<string>? tags = null)
    {
        return new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = category,
            Content = content,
            Importance = importance,
            Confidence = 0.7,
            Embedding = embedding,
            Tags = tags ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };
    }

    [Fact]
    public async Task StoreAsync_AndGetById_ReturnsStoredMemory()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var memory = CreateMemory(userId, "User prefers TypeScript");

        // Act
        await store.StoreAsync(memory);
        var retrieved = await store.GetByIdAsync(userId, memory.Id);

        // Assert
        retrieved.ShouldNotBeNull();
        retrieved.Id.ShouldBe(memory.Id);
        retrieved.Content.ShouldBe("User prefers TypeScript");
        retrieved.UserId.ShouldBe(userId);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";

        // Act
        var result = await store.GetByIdAsync(userId, "non_existent_id");

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsAllUserMemories()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var memory1 = CreateMemory(userId, "Memory 1");
        var memory2 = CreateMemory(userId, "Memory 2");
        var memory3 = CreateMemory(userId, "Memory 3");

        await store.StoreAsync(memory1);
        await store.StoreAsync(memory2);
        await store.StoreAsync(memory3);

        // Act
        var memories = await store.GetByUserIdAsync(userId);

        // Assert
        memories.Count.ShouldBe(3);
        memories.Select(m => m.Content).ShouldContain("Memory 1");
        memories.Select(m => m.Content).ShouldContain("Memory 2");
        memories.Select(m => m.Content).ShouldContain("Memory 3");
    }

    [Fact]
    public async Task GetByUserIdAsync_DifferentUsers_AreIsolated()
    {
        // Arrange
        var store = CreateStore();
        var userId1 = $"user_{Guid.NewGuid():N}";
        var userId2 = $"user_{Guid.NewGuid():N}";

        await store.StoreAsync(CreateMemory(userId1, "User 1 memory"));
        await store.StoreAsync(CreateMemory(userId2, "User 2 memory"));

        // Act
        var user1Memories = await store.GetByUserIdAsync(userId1);
        var user2Memories = await store.GetByUserIdAsync(userId2);

        // Assert
        user1Memories.Count.ShouldBe(1);
        user1Memories[0].Content.ShouldBe("User 1 memory");
        user2Memories.Count.ShouldBe(1);
        user2Memories[0].Content.ShouldBe("User 2 memory");
    }

    [Fact]
    public async Task DeleteAsync_RemovesMemory()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var memory = CreateMemory(userId, "To be deleted");
        await store.StoreAsync(memory);

        // Act
        var deleted = await store.DeleteAsync(userId, memory.Id);
        var retrieved = await store.GetByIdAsync(userId, memory.Id);

        // Assert
        deleted.ShouldBeTrue();
        retrieved.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_ReturnsFalse()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";

        // Act
        var deleted = await store.DeleteAsync(userId, "non_existent");

        // Assert
        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateAccessAsync_UpdatesTimestampAndCount()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var memory = CreateMemory(userId, "Accessed memory");
        await store.StoreAsync(memory);
        var originalAccessTime = memory.LastAccessedAt;

        await Task.Delay(10); // Ensure time difference

        // Act
        var updated = await store.UpdateAccessAsync(userId, memory.Id);
        var retrieved = await store.GetByIdAsync(userId, memory.Id);

        // Assert
        updated.ShouldBeTrue();
        retrieved.ShouldNotBeNull();
        retrieved.AccessCount.ShouldBe(1);
        retrieved.LastAccessedAt.ShouldBeGreaterThan(originalAccessTime);
    }

    [Fact]
    public async Task UpdateImportanceAsync_UpdatesImportance()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var memory = CreateMemory(userId, "Important memory", importance: 0.5);
        await store.StoreAsync(memory);

        // Act
        var updated = await store.UpdateImportanceAsync(userId, memory.Id, 0.9);
        var retrieved = await store.GetByIdAsync(userId, memory.Id);

        // Assert
        updated.ShouldBeTrue();
        retrieved.ShouldNotBeNull();
        retrieved.Importance.ShouldBe(0.9);
    }

    [Fact]
    public async Task UpdateImportanceAsync_ClampsToValidRange()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var memory = CreateMemory(userId, "Clamped memory");
        await store.StoreAsync(memory);

        // Act
        await store.UpdateImportanceAsync(userId, memory.Id, 1.5); // Over max
        var retrieved1 = await store.GetByIdAsync(userId, memory.Id);

        await store.UpdateImportanceAsync(userId, memory.Id, -0.5); // Under min
        var retrieved2 = await store.GetByIdAsync(userId, memory.Id);

        // Assert
        retrieved1!.Importance.ShouldBe(1.0);
        retrieved2!.Importance.ShouldBe(0.0);
    }

    [Fact]
    public async Task SearchAsync_WithCategoryFilter_ReturnsMatchingMemories()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.StoreAsync(CreateMemory(userId, "Preference 1", MemoryCategory.Preference));
        await store.StoreAsync(CreateMemory(userId, "Fact 1"));
        await store.StoreAsync(CreateMemory(userId, "Preference 2", MemoryCategory.Preference));

        // Act
        var results = await store.SearchAsync(userId, categories: [MemoryCategory.Preference]);

        // Assert
        results.Count.ShouldBe(2);
        results.All(r => r.Memory.Category == MemoryCategory.Preference).ShouldBeTrue();
    }

    [Fact]
    public async Task SearchAsync_WithMinImportance_FiltersLowImportance()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.StoreAsync(CreateMemory(userId, "Low importance", importance: 0.3));
        await store.StoreAsync(CreateMemory(userId, "High importance", importance: 0.9));

        // Act
        var results = await store.SearchAsync(userId, minImportance: 0.5);

        // Assert
        results.Count.ShouldBe(1);
        results[0].Memory.Content.ShouldBe("High importance");
    }

    [Fact]
    public async Task SearchAsync_WithKeywordQuery_FindsMatchingContent()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.StoreAsync(CreateMemory(userId, "User works with Python"));
        await store.StoreAsync(CreateMemory(userId, "User likes TypeScript"));
        await store.StoreAsync(CreateMemory(userId, "User prefers VS Code"));

        // Act
        var results = await store.SearchAsync(userId, query: "Python");

        // Assert
        results.Count.ShouldBe(1);
        results[0].Memory.Content.ShouldContain("Python");
    }

    [Fact]
    public async Task SearchAsync_WithTagFilter_ReturnsMatchingMemories()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.StoreAsync(CreateMemory(userId, "Tagged memory", tags: ["coding", "preference"]));
        await store.StoreAsync(CreateMemory(userId, "Untagged memory"));

        // Act
        var results = await store.SearchAsync(userId, tags: ["coding"]);

        // Assert
        results.Count.ShouldBe(1);
        results[0].Memory.Content.ShouldBe("Tagged memory");
    }

    [Fact]
    public async Task SearchAsync_WithEmbedding_RanksResultsBySimilarity()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";

        var similar = CreateMemory(userId, "Similar content",
            embedding: CreateTestEmbedding(0.9f)); // Primary value at index 0
        var different = CreateMemory(userId, "Different content",
            embedding: CreateTestEmbedding(0.9f, 500)); // Primary value at index 500
        await store.StoreAsync(similar);
        await store.StoreAsync(different);

        // Query embedding close to "similar" - primary value at index 0
        var queryEmbedding = CreateTestEmbedding();

        // Act
        var results = await store.SearchAsync(userId, queryEmbedding: queryEmbedding);

        // Assert
        results.Count.ShouldBe(2);
        results[0].Memory.Content.ShouldBe("Similar content"); // Higher similarity first
    }

    [Fact]
    public async Task SearchAsync_WithLimit_ReturnsLimitedResults()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        for (var i = 0; i < 10; i++)
        {
            await store.StoreAsync(CreateMemory(userId, $"Memory {i}"));
        }

        // Act
        var results = await store.SearchAsync(userId, limit: 5);

        // Assert
        results.Count.ShouldBe(5);
    }

    [Fact]
    public async Task SaveProfileAsync_AndGetProfile_Works()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var profile = new PersonalityProfile
        {
            UserId = userId,
            Summary = "Technical user who prefers concise responses",
            InteractionGuidelines = ["Skip basic explanations", "Include code examples"],
            Confidence = 0.85,
            BasedOnMemoryCount = 20,
            LastUpdated = DateTimeOffset.UtcNow
        };

        // Act
        await store.SaveProfileAsync(profile);
        var retrieved = await store.GetProfileAsync(userId);

        // Assert
        retrieved.ShouldNotBeNull();
        retrieved.Summary.ShouldBe("Technical user who prefers concise responses");
        retrieved.InteractionGuidelines.Count.ShouldBe(2);
        retrieved.Confidence.ShouldBe(0.85);
    }

    [Fact]
    public async Task GetProfileAsync_NonExistent_ReturnsNull()
    {
        // Arrange
        var store = CreateStore();

        // Act
        var result = await store.GetProfileAsync("non_existent_user");

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllUserIdsAsync_UserWithOnlyProfile_IsIncluded()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.SaveProfileAsync(new PersonalityProfile
        {
            UserId = userId,
            Summary = "Orphaned profile holder",
            LastUpdated = DateTimeOffset.UtcNow
        });

        // Act
        var userIds = await store.GetAllUserIdsAsync();

        // Assert
        userIds.ShouldContain(userId);
    }

    [Fact]
    public async Task GetAllUserIdsAsync_ProfileKey_IsNeverReadAsAUserNamedProfile()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.SaveProfileAsync(new PersonalityProfile
        {
            UserId = userId,
            Summary = "Any profile",
            LastUpdated = DateTimeOffset.UtcNow
        });

        // Act
        var userIds = await store.GetAllUserIdsAsync();

        // Assert
        userIds.ShouldNotContain("profile");
    }

    [Fact]
    public async Task DeleteProfileAsync_RemovesProfile()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.SaveProfileAsync(new PersonalityProfile
        {
            UserId = userId,
            Summary = "To be removed",
            LastUpdated = DateTimeOffset.UtcNow
        });

        // Act
        var deleted = await store.DeleteProfileAsync(userId);
        var retrieved = await store.GetProfileAsync(userId);

        // Assert
        deleted.ShouldBeTrue();
        retrieved.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteProfileAsync_NonExistent_ReturnsFalse()
    {
        // Arrange
        var store = CreateStore();

        // Act
        var deleted = await store.DeleteProfileAsync($"user_{Guid.NewGuid():N}");

        // Assert
        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectCounts()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        await store.StoreAsync(CreateMemory(userId, "Pref 1", MemoryCategory.Preference));
        await store.StoreAsync(CreateMemory(userId, "Pref 2", MemoryCategory.Preference));
        await store.StoreAsync(CreateMemory(userId, "Fact 1"));
        await store.StoreAsync(CreateMemory(userId, "Project 1", MemoryCategory.Project));

        // Act
        var stats = await store.GetStatsAsync(userId);

        // Assert
        stats.TotalMemories.ShouldBe(4);
        stats.ByCategory[MemoryCategory.Preference].ShouldBe(2);
        stats.ByCategory[MemoryCategory.Fact].ShouldBe(1);
        stats.ByCategory[MemoryCategory.Project].ShouldBe(1);
    }

    [Fact]
    public async Task GetStatsAsync_ExcludesDeletedMemories()
    {
        // Arrange
        var store = CreateStore();
        var userId = $"user_{Guid.NewGuid():N}";
        var oldMemory = CreateMemory(userId, "Old");
        var newMemory = CreateMemory(userId, "New");
        await store.StoreAsync(oldMemory);
        await store.StoreAsync(newMemory);
        await store.DeleteAsync(userId, oldMemory.Id);

        // Act
        var stats = await store.GetStatsAsync(userId);

        // Assert
        stats.TotalMemories.ShouldBe(1);
    }
}