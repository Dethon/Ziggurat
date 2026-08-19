using Domain.Contracts;
using Domain.DTOs;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The remembered user, held in memory for one run. Recall itself is injected as data — the block
// rides the turn the way production's recall hook puts it there — so what this store is for is the
// other half: `memory_forget` searches it and deletes what it found, and only the store afterwards
// says which facts went.
//
// Faked rather than taken from Redis because retrieval quality is out of scope for this suite and
// the real store's search is a k-nearest query with no relevance floor: against a handful of
// seeded facts it returns all of them, and no scenario could say which fact the turn was about.
// The matching here is lexical, which means a scenario has to seed a fact whose own words a
// plausible query would use — a query that matched nothing shows up as the stale fact surviving,
// with the query itself in the dump.
//
// The unbounded delete that used to sit behind a forget-by-query (finding 02) is fixed in the
// tool: a query that reaches more than one memory deletes nothing and returns the candidates, so
// against this store a scenario's forget only lands when its query matches exactly the fact it is
// about — which is what the lexical matching was already for. The tool's behaviour against the
// real store is pinned by MemoryForgetToolRedisTests; what these scenarios are evidence about is
// the agent's decision to forget, never the store's decision about what that means.
public sealed class EvalMemory(IReadOnlyList<RememberedFact> facts) : IMemoryStore, IEmbeddingService
{
    private readonly Dictionary<string, MemoryEntry> _entries = facts
        .Select((fact, index) => Entry(fact, index))
        .ToDictionary(entry => entry.Id);

    private readonly Lock _gate = new();

    // What the turn left behind, in the order the facts were declared, for the check that decides
    // whether the right one went.
    public IReadOnlyList<string> Remaining
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values.OrderBy(e => e.CreatedAt).Select(e => e.Content)];
            }
        }
    }

    // What rides the turn, or nothing at all: production's recall hook sets a context only when
    // there is something in it, so a scenario that remembers nothing must decorate its turn with
    // nothing — an empty block is a change to every turn in the suite.
    public MemoryContext? Context
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count == 0 ? null : new MemoryContext(
                    [.. _entries.Values.OrderBy(e => e.CreatedAt).Select(e => new MemorySearchResult(e, 1.0))],
                    null);
            }
        }
    }

    private static MemoryEntry Entry(RememberedFact fact, int index) => new()
    {
        Id = $"eval-memory-{index}",
        UserId = EvalRun.UserId,
        Category = fact.Category,
        Content = fact.Content,
        Importance = fact.Importance,
        Confidence = 1.0,
        // Spread so the declared order survives the round trip; the exact dates are never read.
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index),
        LastAccessedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index)
    };

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string userId, string? query = null, float[]? queryEmbedding = null,
        IEnumerable<MemoryCategory>? categories = null, IEnumerable<string>? tags = null,
        double? minImportance = null, int limit = 10, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var wanted = Words(query);
            IReadOnlyList<MemorySearchResult> matches =
            [
                .. _entries.Values
                    .Where(entry => entry.UserId == userId)
                    .Where(entry => categories is null || categories.Contains(entry.Category))
                    .Select(entry => new MemorySearchResult(entry, Overlap(wanted, Words(entry.Content))))
                    .Where(result => result.Relevance > 0)
                    .OrderByDescending(result => result.Relevance)
                    .Take(limit)
            ];

            return Task.FromResult(matches);
        }
    }

    // Words of four letters or more, compared on their first four: what a correction says
    // ("ya no trabajo en Acme") and what the memory says ("Trabaja en Acme") rarely agree past the
    // stem, and the short words they do share say nothing about the subject.
    private static IReadOnlyList<string> Words(string? text) =>
        [.. (text ?? "")
            .ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '?', '!', '¿', '¡', '"', '\'', '(', ')', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4)
            .Select(word => word[..4])];

    private static double Overlap(IReadOnlyList<string> query, IReadOnlyList<string> content) =>
        query.Count == 0 ? 0 : query.Intersect(content).Count() / (double)query.Count;

    public Task<bool> DeleteAsync(string userId, string memoryId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Remove(memoryId));
        }
    }

    public Task<MemoryEntry?> GetByIdAsync(string userId, string memoryId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.GetValueOrDefault(memoryId));
        }
    }

    public Task<IReadOnlyList<MemoryEntry>> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<MemoryEntry> all = [.. _entries.Values.Where(e => e.UserId == userId)];
            return Task.FromResult(all);
        }
    }

    public Task<bool> HasAnyMemoriesAsync(string userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Values.Any(e => e.UserId == userId));
        }
    }

    public Task<MemoryEntry> StoreAsync(MemoryEntry memory, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _entries[memory.Id] = memory;
            return Task.FromResult(memory);
        }
    }

    public Task<MemoryStats> GetStatsAsync(string userId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var mine = _entries.Values.Where(e => e.UserId == userId).ToList();
            return Task.FromResult(new MemoryStats(
                mine.Count,
                mine.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Count())));
        }
    }

    public Task<bool> UpdateAccessAsync(string userId, string memoryId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> UpdateImportanceAsync(
        string userId, string memoryId, double importance, CancellationToken ct = default) =>
        Task.FromResult(true);

    // No profile and no synthesis: a profile is written by the dreaming service, which does not run
    // in an eval, and a scenario that wanted one would declare it as a fact.
    public Task<PersonalityProfile?> GetProfileAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<PersonalityProfile?>(null);

    public Task<PersonalityProfile> SaveProfileAsync(PersonalityProfile profile, CancellationToken ct = default) =>
        Task.FromResult(profile);

    public Task<bool> DeleteProfileAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<string>> GetAllUserIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([EvalRun.UserId]);

    // Nothing embeds here, which is the point: an eval that reached a real embedding server would
    // spend a request on a subject this suite has ruled out. An empty vector is what the search
    // above ignores anyway.
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<float[]>([]);

    public Task<float[][]> GenerateEmbeddingsAsync(
        IEnumerable<string> texts, CancellationToken ct = default) =>
        Task.FromResult(texts.Select(_ => Array.Empty<float>()).ToArray());
}