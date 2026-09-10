using System.Collections.Concurrent;

namespace Infrastructure.Agents.Mcp;

// What a server serves at warmup — its prompts, and its skills — kept for a short while so a burst
// of session builds does not fetch the same words from the same server each time. Keyed by the
// caller, so a server's prompts and its skills are two entries with one policy.
public sealed class McpPromptCache(TimeProvider timeProvider, TimeSpan ttl)
{
    // The value's type travels with it. One dictionary holds every caller's shape, so the type a
    // key was stored under is the caller's promise and this is what keeps it: two callers sharing
    // a key by accident is a wiring mistake, and it should say so by name rather than surface as a
    // bare cast in the middle of a session build.
    private sealed record CacheEntry(object Value, Type Type, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, Task> _refreshes = new();

    public async Task<T> GetOrFetchAsync<T>(
        string serverKey, Func<CancellationToken, Task<T>> fetch, CancellationToken ct) where T : class
    {
        if (!_entries.TryGetValue(serverKey, out var entry))
        {
            var fetched = await fetch(ct);
            _entries[serverKey] = new CacheEntry(fetched, typeof(T), timeProvider.GetUtcNow());
            return fetched;
        }

        // Before the refresh, not after: a caller asking under the wrong type would otherwise
        // start a background fetch that overwrites the entry with its own shape, and the throw
        // would leave the real owner's next read broken.
        if (entry.Type != typeof(T))
        {
            throw KeyReused(serverKey, entry.Type, typeof(T));
        }

        if (timeProvider.GetUtcNow() - entry.FetchedAt >= ttl)
        {
            // Stale: serve the cached value now, refresh in the background (single-flight per
            // server). A failed refresh keeps the stale value; the next stale hit retries.
            // GetOrAdd may invoke the factory twice under a tight race; both refreshes run and
            // one guard entry is dropped. A duplicate fetch is harmless — it's idempotent.
            // (Don't "fix" with Lazy<Task>: a faulted Lazy would never retry.)
            _ = _refreshes.GetOrAdd(serverKey, key => Task.Run(async () =>
            {
                try
                {
                    // Deliberately not the caller's token: the triggering session may end
                    // (or its client be disposed) before the refresh completes — both fail
                    // the fetch harmlessly and the next stale hit retries.
                    var fetched = await fetch(CancellationToken.None);
                    _entries[key] = new CacheEntry(fetched, typeof(T), timeProvider.GetUtcNow());
                }
                catch
                {
                    // Stale prompts beat a blocked or failed session build.
                }
                finally
                {
                    _refreshes.TryRemove(key, out _);
                }
            }));
        }

        return entry.Type == typeof(T)
            ? (T)entry.Value
            : throw KeyReused(serverKey, entry.Type, typeof(T));
    }

    // Both types named, because the fix is to give one of the two callers its own key.
    private static InvalidOperationException KeyReused(string serverKey, Type stored, Type asked) =>
        new($"The cache key '{serverKey}' holds {stored.Name} and was fetched as {asked.Name}. "
            + "Two callers are sharing one key; give each its own.");
}