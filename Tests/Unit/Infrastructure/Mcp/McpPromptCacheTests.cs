using Domain.Prompts;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

public class McpPromptCacheTests
{
    private static readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

    // The cache stores whatever the fetch returned; what a section is bound to does not matter to
    // it, so one declaration serves every case here.
    private static PromptSection[] Sections(params string[] texts) =>
        [.. texts.Select(t => PromptManifest.Bind("test_prompt", t))];

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
        McpPromptCache cache, Func<CancellationToken, Task<PromptSection[]>> fetch, string[] expected) =>
        WaitUntilAsync(() =>
            cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)
                .GetAwaiter().GetResult().Select(p => p.Text).SequenceEqual(expected));

    [Fact]
    public async Task GetOrFetchAsync_FreshHit_DoesNotRefetch()
    {
        var cache = new McpPromptCache(new FakeTimeProvider(), _ttl);
        var fetches = 0;
        Task<PromptSection[]> fetch(CancellationToken ct)
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(Sections("p1"));
        }

        await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);
        var second = await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);

        second.Select(p => p.Text).ShouldBe(["p1"]);
        fetches.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleHit_ServesStaleAndRefreshesInBackground()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<PromptSection[]> fetch(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref fetches);
            return Task.FromResult(Sections($"v{n}"));
        }

        (await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)).Select(p => p.Text).ShouldBe(["v1"]);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));

        var staleServed = await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);

        staleServed.Select(p => p.Text).ShouldBe(["v1"], "a stale hit must serve the cached value without blocking");
        await WaitForCachedValueAsync(cache, fetch, ["v2"]);
    }

    [Fact]
    public async Task GetOrFetchAsync_RefreshFails_KeepsServingStaleValue()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<PromptSection[]> fetch(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref fetches);
            return n == 1
                ? Task.FromResult(Sections("v1"))
                : Task.FromException<PromptSection[]>(new HttpRequestException("server down"));
        }

        await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));

        (await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)).Select(p => p.Text).ShouldBe(["v1"]);
        await WaitUntilAsync(() => Volatile.Read(ref fetches) >= 2);
        (await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None)).Select(p => p.Text).ShouldBe(["v1"]);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleHitWithCancelledCaller_BackgroundRefreshStillCompletes()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<PromptSection[]> fetch(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref fetches);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Sections($"v{n}"));
        }

        await cache.GetOrFetchAsync("server-a", fetch, CancellationToken.None);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Stale hit with an already-cancelled caller: serve stale now, refresh must still run.
        (await cache.GetOrFetchAsync("server-a", fetch, cancelled.Token)).Select(p => p.Text).ShouldBe(["v1"]);

        await WaitForCachedValueAsync(cache, fetch, ["v2"]);
    }
}