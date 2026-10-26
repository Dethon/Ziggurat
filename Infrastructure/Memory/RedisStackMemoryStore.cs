using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace Infrastructure.Memory;

public class RedisStackMemoryStore : IMemoryStore
{
    public const string IndexName = "idx:memories";
    private const string ProfilePrefix = "memory:profile:";
    private static readonly TimeSpan _defaultExpiry = TimeSpan.FromDays(365);

    private readonly IDatabase _db;
    private readonly SearchCommands _ft;
    private readonly IServer _server;
    private readonly int _vectorDimension;
    private bool _indexInitialized;

    public RedisStackMemoryStore(IConnectionMultiplexer redis, EmbeddingOptions embeddingOptions)
    {
        _db = redis.GetDatabase();
        _ft = _db.FT();
        _server = redis.GetServer(redis.GetEndPoints()[0]);
        // Follows the embedding model rather than being a constant here: changing the model
        // is then a configuration change plus a migration, not an edit inside a store.
        _vectorDimension = embeddingOptions.Dimension;
    }

    public async Task<MemoryEntry> StoreAsync(MemoryEntry memory, CancellationToken ct = default)
    {
        await EnsureIndexCreatedAsync();

        var key = MemoryKey(memory.UserId, memory.Id);
        await _db.HashSetAsync(key, MemorySerializer.ToHashEntries(memory));
        await _db.KeyExpireAsync(key, _defaultExpiry);

        return memory;
    }

    public async Task<MemoryEntry?> GetByIdAsync(string userId, string memoryId, CancellationToken ct = default)
    {
        var hash = await _db.HashGetAllAsync(MemoryKey(userId, memoryId));
        return hash.Length == 0 ? null : MemorySerializer.FromHash(hash);
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var memories = new List<MemoryEntry>();

        await foreach (var key in _server.KeysAsync(pattern: $"memory:{userId}:*").WithCancellation(ct))
        {
            var hash = await _db.HashGetAllAsync(key);
            if (hash.Length == 0)
            {
                continue;
            }

            var memory = MemorySerializer.FromHash(hash);
            if (memory is not null)
            {
                memories.Add(memory);
            }
        }

        return memories.OrderByDescending(m => m.CreatedAt).ToList();
    }

    public async Task<bool> HasAnyMemoriesAsync(string userId, CancellationToken ct = default)
    {
        // Asked through the index rather than by scanning keys. A MATCH'd SCAN filters
        // server-side but still walks the whole keyspace, and for an unremembered user —
        // the case this exists to make fast — nothing matches, so it walks all of it. That
        // keyspace also holds the transcripts and thread state, and this runs on the
        // blocking path of every turn.
        await EnsureIndexCreatedAsync();

        try
        {
            // No documents and no fields: the count is the whole answer.
            var query = new Query($"@userId:{{{EscapeTag(userId)}}}")
                .Limit(0, 0)
                .Dialect(2);
            var result = await _ft.SearchAsync(IndexName, query);
            return result.TotalResults > 0;
        }
        catch (RedisServerException)
        {
            // An unusable index must not decide that a user has nothing; recall then falls
            // through to a search that reports the real failure.
            return true;
        }
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string userId,
        string? query = null,
        float[]? queryEmbedding = null,
        IEnumerable<MemoryCategory>? categories = null,
        IEnumerable<string>? tags = null,
        double? minImportance = null,
        int limit = 10,
        CancellationToken ct = default)
    {
        var filter = new MemoryFilter(categories, tags, minImportance);

        if (queryEmbedding is { Length: > 0 })
        {
            await EnsureIndexCreatedAsync();
            return await VectorSearchAsync(userId, queryEmbedding, filter, limit);
        }

        return FilteredSearch(await GetByUserIdAsync(userId, ct), filter, query, limit);
    }

    public async Task<bool> DeleteAsync(string userId, string memoryId, CancellationToken ct = default)
    {
        return await _db.KeyDeleteAsync(MemoryKey(userId, memoryId));
    }

    public async Task<bool> UpdateAccessAsync(string userId, string memoryId, CancellationToken ct = default)
    {
        var memory = await GetByIdAsync(userId, memoryId, ct);
        if (memory is null)
        {
            return false;
        }

        return await UpdateMemory(memory with
        {
            LastAccessedAt = DateTimeOffset.UtcNow,
            AccessCount = memory.AccessCount + 1
        }, ct);
    }

    public async Task<bool> UpdateImportanceAsync(string userId, string memoryId, double importance,
        CancellationToken ct = default)
    {
        var memory = await GetByIdAsync(userId, memoryId, ct);
        if (memory is null)
        {
            return false;
        }

        return await UpdateMemory(memory with { Importance = Math.Clamp(importance, 0, 1) }, ct);
    }

    public async Task<PersonalityProfile?> GetProfileAsync(string userId, CancellationToken ct = default)
    {
        var json = await _db.StringGetAsync(ProfileKey(userId));
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<PersonalityProfile>((string)json!);
    }

    public async Task<PersonalityProfile> SaveProfileAsync(PersonalityProfile profile, CancellationToken ct = default)
    {
        await _db.StringSetAsync(ProfileKey(profile.UserId), JsonSerializer.Serialize(profile), _defaultExpiry);
        return profile;
    }

    public async Task<bool> DeleteProfileAsync(string userId, CancellationToken ct = default)
    {
        return await _db.KeyDeleteAsync(ProfileKey(userId));
    }

    public async Task<MemoryStats> GetStatsAsync(string userId, CancellationToken ct = default)
    {
        var memories = await GetByUserIdAsync(userId, ct);

        return new MemoryStats(
            memories.Count,
            memories.GroupBy(m => m.Category).ToDictionary(g => g.Key, g => g.Count()));
    }

    public async Task<IReadOnlyList<string>> GetAllUserIdsAsync(CancellationToken ct = default)
    {
        var userIds = new HashSet<string>();
        await foreach (var key in _server.KeysAsync(pattern: "memory:*:*").WithCancellation(ct))
        {
            var keyText = key.ToString();
            if (keyText.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            {
                userIds.Add(keyText[ProfilePrefix.Length..]);
            }
            else if (keyText.Split(':') is { Length: >= 3 } parts)
            {
                userIds.Add(parts[1]);
            }
        }

        return userIds.ToList();
    }

    private async Task<bool> UpdateMemory(MemoryEntry memory, CancellationToken ct)
    {
        await StoreAsync(memory, ct);
        return true;
    }

    private async Task<IReadOnlyList<MemorySearchResult>> VectorSearchAsync(
        string userId,
        float[] queryEmbedding,
        MemoryFilter filter,
        int limit)
    {
        var filterQuery = BuildFilterQuery(userId, filter);
        var knnQuery = $"({filterQuery})=>[KNN {limit * 2} @embedding $BLOB AS vector_score]";

        var searchQuery = new Query(knnQuery)
            .AddParam("BLOB", VectorSerializer.ToBytes(queryEmbedding))
            .SetSortBy("vector_score")
            .Limit(0, limit * 2)
            .Dialect(2);

        var result = await _ft.SearchAsync(IndexName, searchQuery);

        return result.Documents
            .Select(ParseSearchDocument)
            .Where(r => r.Memory is not null)
            .OrderByDescending(r => r.Relevance)
            .Take(limit)
            .ToList();
    }

    private static IReadOnlyList<MemorySearchResult> FilteredSearch(
        IReadOnlyList<MemoryEntry> memories,
        MemoryFilter filter,
        string? query,
        int limit)
    {
        var filtered = memories.Where(filter.Matches);

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderByDescending(m => m.Importance)
            .Take(limit)
            .Select(m => new MemorySearchResult(m, m.Importance))
            .ToList();
    }

    private static string BuildFilterQuery(string userId, MemoryFilter filter)
    {
        var filters = new List<string> { $"@userId:{{{EscapeTag(userId)}}}" };

        if (filter.Categories is { Length: > 0 })
        {
            filters.Add($"@category:{{{string.Join("|", filter.Categories)}}}");
        }

        if (filter.Tags is { Length: > 0 })
        {
            filters.Add($"@tags:{{{string.Join("|", filter.Tags.Select(EscapeTag))}}}");
        }

        if (filter.MinImportance.HasValue)
        {
            filters.Add($"@importance:[{filter.MinImportance.Value} +inf]");
        }

        return string.Join(" ", filters);
    }

    private static MemorySearchResult ParseSearchDocument(Document doc)
    {
        var props = doc.GetProperties().ToDictionary(p => p.Key, p => p.Value);
        var memory = MemorySerializer.FromDictionary(props);

        var distance = props.TryGetValue("vector_score", out var score) && score.HasValue ? (double)score : 1.0;
        var similarity = 1.0 - distance;
        var weightedScore = memory is not null ? similarity * memory.Importance : 0;

        return new MemorySearchResult(memory!, weightedScore) { Similarity = similarity };
    }

    private async Task EnsureIndexCreatedAsync()
    {
        if (_indexInitialized)
        {
            return;
        }

        try
        {
            await _ft.InfoAsync(IndexName);
            _indexInitialized = true;
        }
        catch (RedisServerException)
        {
            await CreateIndexAsync();
            _indexInitialized = true;
        }
    }

    private async Task CreateIndexAsync()
    {
        var schema = new Schema()
            .AddTagField("userId", separator: "|")
            .AddTextField("content")
            .AddTagField("category", separator: ",")
            .AddTagField("tags", separator: ",")
            .AddNumericField("importance", sortable: true)
            .AddNumericField("confidence")
            .AddNumericField("createdAt", sortable: true)
            .AddNumericField("lastAccessedAt", sortable: true)
            .AddNumericField("accessCount")
            .AddVectorField("embedding", Schema.VectorField.VectorAlgo.HNSW, new Dictionary<string, object>
            {
                ["TYPE"] = "FLOAT32",
                ["DIM"] = _vectorDimension,
                ["DISTANCE_METRIC"] = "COSINE"
            });

        await _ft.CreateAsync(IndexName, new FTCreateParams().On(IndexDataType.HASH).Prefix("memory:"), schema);
    }

    private static string MemoryKey(string userId, string memoryId)
    {
        return $"memory:{userId}:{memoryId}";
    }

    private static string ProfileKey(string userId)
    {
        return $"{ProfilePrefix}{userId}";
    }

    private static string EscapeTag(string value)
    {
        return value.Replace("-", "\\-").Replace(":", "\\:");
    }

    private sealed record MemoryFilter(
        MemoryCategory[]? Categories,
        string[]? Tags,
        double? MinImportance)
    {
        public MemoryFilter(
            IEnumerable<MemoryCategory>? categories,
            IEnumerable<string>? tags,
            double? minImportance)
            : this(
                categories?.ToArray(),
                tags?.ToArray(),
                minImportance)
        {
        }

        public bool Matches(MemoryEntry m)
        {
            return (Categories is null || Categories.Contains(m.Category)) &&
                   (Tags is null || m.Tags.Any(t => Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) &&
                   (MinImportance is null || m.Importance >= MinImportance);
        }
    }

    private static class MemorySerializer
    {
        public static HashEntry[] ToHashEntries(MemoryEntry m)
        {
            return
            [
                new HashEntry("userId", m.UserId),
                new HashEntry("memoryId", m.Id),
                new HashEntry("content", m.Content),
                new HashEntry("context", m.Context ?? ""),
                new HashEntry("category", m.Category.ToString()),
                new HashEntry("tags", string.Join(",", m.Tags)),
                new HashEntry("importance", m.Importance),
                new HashEntry("confidence", m.Confidence),
                new HashEntry("createdAt", m.CreatedAt.ToUnixTimeMilliseconds()),
                new HashEntry("lastAccessedAt", m.LastAccessedAt.ToUnixTimeMilliseconds()),
                new HashEntry("accessCount", m.AccessCount),
                new HashEntry("embedding",
                    m.Embedding != null ? VectorSerializer.ToBytes(m.Embedding) : Array.Empty<byte>()),
                new HashEntry("sourceJson", m.Source != null ? JsonSerializer.Serialize(m.Source) : "")
            ];
        }

        public static MemoryEntry? FromHash(HashEntry[] hash)
        {
            return FromDictionary(hash.ToDictionary(h => h.Name.ToString(), h => h.Value));
        }

        public static MemoryEntry? FromDictionary(Dictionary<string, RedisValue> d)
        {
            if (!d.TryGetValue("memoryId", out var memoryId) || memoryId.IsNullOrEmpty)
            {
                return null;
            }

            if (!d.TryGetValue("userId", out var userId) || userId.IsNullOrEmpty)
            {
                return null;
            }

            Enum.TryParse<MemoryCategory>(d.GetValueOrDefault("category", "Fact").ToString(), out var category);

            var sourceJson = d.GetValueOrDefault("sourceJson", "").ToString();

            return new MemoryEntry
            {
                Id = memoryId.ToString(),
                UserId = userId.ToString(),
                Content = d.GetValueOrDefault("content", "").ToString(),
                Context = d.GetValueOrDefault("context", "").ToString(),
                Category = category,
                Tags = ParseTags(d.GetValueOrDefault("tags", "").ToString()),
                Importance = (double)d.GetValueOrDefault("importance", 0.5),
                Confidence = (double)d.GetValueOrDefault("confidence", 0.7),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)d.GetValueOrDefault("createdAt", 0)),
                LastAccessedAt =
                    DateTimeOffset.FromUnixTimeMilliseconds((long)d.GetValueOrDefault("lastAccessedAt", 0)),
                AccessCount = (int)d.GetValueOrDefault("accessCount", 0),
                Embedding = VectorSerializer.FromBytes(d.GetValueOrDefault("embedding", RedisValue.Null)),
                Source = string.IsNullOrEmpty(sourceJson) ? null : JsonSerializer.Deserialize<MemorySource>(sourceJson)
            };
        }

        private static string[] ParseTags(string tags)
        {
            return string.IsNullOrEmpty(tags) ? [] : tags.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private static class VectorSerializer
    {
        public static byte[] ToBytes(float[] vector)
        {
            var bytes = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float[]? FromBytes(RedisValue value)
        {
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            var bytes = (byte[])value!;
            if (bytes.Length == 0)
            {
                return null;
            }

            var vector = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            return vector;
        }
    }
}