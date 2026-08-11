using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

public class McpPromptCacheTests
{
    private static readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }

    // The fetch counter rises before the background refresh stores the new entry, so waiting on
    // the counter and then reading the cache can still observe the stale value. Wait on the value.
    private static Task WaitForCachedValueAsync(
        McpPromptCache cache, Func<CancellationToken, Task<string[]>> fetch, string[] expected) =>
        WaitUntilAsync(() =>
            cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)
                .GetAwaiter().GetResult().SequenceEqual(expected));

    [Fact]
    public async Task GetOrFetchAsync_FreshHit_DoesNotRefetch()
    {
        var cache = new McpPromptCache(new FakeTimeProvider(), _ttl);
        var fetches = 0;
        Task<string[]> fetch(CancellationToken ct)
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(new[] { "p1" });
        }

        await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);
        var second = await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);

        second.ShouldBe(["p1"]);
        fetches.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleHit_ServesStaleAndRefreshesInBackground()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<string[]> fetch(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref fetches);
            return Task.FromResult(new[] { $"v{n}" });
        }

        (await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)).ShouldBe(["v1"]);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));

        var staleServed = await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);

        staleServed.ShouldBe(["v1"], "a stale hit must serve the cached value without blocking");
        await WaitForCachedValueAsync(cache, fetch, ["v2"]);
    }

    [Fact]
    public async Task GetOrFetchAsync_RefreshFails_KeepsServingStaleValue()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<string[]> fetch(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref fetches);
            return n == 1
                ? Task.FromResult(new[] { "v1" })
                : Task.FromException<string[]>(new HttpRequestException("server down"));
        }

        await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));

        (await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)).ShouldBe(["v1"]);
        await WaitUntilAsync(() => Volatile.Read(ref fetches) >= 2);
        (await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)).ShouldBe(["v1"]);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleHitWithCancelledCaller_BackgroundRefreshStillCompletes()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<string[]> fetch(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref fetches);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new[] { $"v{n}" });
        }

        await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Stale hit with an already-cancelled caller: serve stale now, refresh must still run.
        (await cache.GetOrFetchAsync("server-a", fetch, cancelled.Token)).ShouldBe(["v1"]);

        await WaitForCachedValueAsync(cache, fetch, ["v2"]);
    }
}