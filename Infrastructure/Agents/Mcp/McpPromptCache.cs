using System.Collections.Concurrent;

namespace Infrastructure.Agents.Mcp;

// What a server serves at warmup — its prompts, and its skills — kept for a short while so a burst
// of session builds does not fetch the same words from the same server each time. Keyed by the
// caller, so a server's prompts and its skills are two entries with one policy.
public sealed class McpPromptCache(TimeProvider timeProvider, TimeSpan ttl)
{
    private sealed record CacheEntry(object Value, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, Task> _refreshes = new();

    public async Task<T> GetOrFetchAsync<T>(
        string serverKey, Func<CancellationToken, Task<T>> fetch, CancellationToken ct) where T : class
    {
        if (!_entries.TryGetValue(serverKey, out var entry))
        {
            var fetched = await fetch(ct);
            _entries[serverKey] = new CacheEntry(fetched, timeProvider.GetUtcNow());
            return fetched;
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
                    _entries[key] = new CacheEntry(fetched, timeProvider.GetUtcNow());
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

        return (T)entry.Value;
    }
}