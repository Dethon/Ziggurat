using Domain.DTOs;

namespace Domain.Contracts;

public interface IMemoryStore
{
    Task<MemoryEntry> StoreAsync(MemoryEntry memory, CancellationToken ct = default);
    Task<MemoryEntry?> GetByIdAsync(string userId, string memoryId, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntry>> GetByUserIdAsync(string userId, CancellationToken ct = default);

    // Whether the user has any memory entries at all, without reading them. An unremembered
    // user is one for which this is false.
    Task<bool> HasAnyMemoriesAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string userId,
        string? query = null,
        float[]? queryEmbedding = null,
        IEnumerable<MemoryCategory>? categories = null,
        IEnumerable<string>? tags = null,
        double? minImportance = null,
        int limit = 10,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(string userId, string memoryId, CancellationToken ct = default);

    Task<bool> UpdateAccessAsync(string userId, string memoryId, CancellationToken ct = default);
    Task<bool> UpdateImportanceAsync(string userId, string memoryId, double importance, CancellationToken ct = default);

    Task<PersonalityProfile?> GetProfileAsync(string userId, CancellationToken ct = default);
    Task<PersonalityProfile> SaveProfileAsync(PersonalityProfile profile, CancellationToken ct = default);
    Task<bool> DeleteProfileAsync(string userId, CancellationToken ct = default);

    Task<MemoryStats> GetStatsAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetAllUserIdsAsync(CancellationToken ct = default);
}

// Relevance is what results are ranked by: similarity weighted by importance. Similarity is the
// raw cosine a vector search measured, which is what says whether a memory is about the query at
// all; null where the search measured none, as the keyword path does.
public record MemorySearchResult(MemoryEntry Memory, double Relevance)
{
    public double? Similarity { get; init; }
}

public record MemoryStats(
    int TotalMemories,
    IReadOnlyDictionary<MemoryCategory, int> ByCategory);