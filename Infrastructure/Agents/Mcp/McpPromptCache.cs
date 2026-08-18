using System.Collections.Concurrent;
using Domain.Prompts;

namespace Infrastructure.Agents.Mcp;

public sealed class McpPromptCache(TimeProvider timeProvider, TimeSpan ttl)
{
    private sealed record CacheEntry(PromptSection[] Prompts, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, Task> _refreshes = new();

    public async Task<PromptSection[]> GetOrFetchAsync(
        string serverKey, Func<CancellationToken, Task<PromptSection[]>> fetch, CancellationToken ct)
    {
        if (!_entries.TryGetValue(serverKey, out var entry))
        {
            var prompts = await fetch(ct);
            _entries[serverKey] = new CacheEntry(prompts, timeProvider.GetUtcNow());
            return prompts;
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
                    var prompts = await fetch(CancellationToken.None);
                    _entries[key] = new CacheEntry(prompts, timeProvider.GetUtcNow());
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

        return entry.Prompts;
    }
}