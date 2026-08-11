using Infrastructure.Memory;
using NRedisStack.RedisStackCommands;
using Shouldly;

namespace Tests.Integration.Fixtures;

// What the Redis fixtures promise once they stopped being one container per test class: the run
// keeps a handful of containers, and every class still gets a keyspace nobody else writes to.
[Trait("Category", "Integration")]
public class RedisSharingTests
{
    [Fact]
    public async Task TwoFixtures_ShareTheContainer_ButNotTheDatabase()
    {
        var first = new RedisFixture();
        var second = new RedisFixture();
        await first.InitializeAsync();
        await second.InitializeAsync();

        try
        {
            second.Endpoint.ShouldBe(first.Endpoint);

            var firstDb = first.Connection.GetDatabase();
            var secondDb = second.Connection.GetDatabase();
            secondDb.Database.ShouldNotBe(firstDb.Database);

            // The same key name in both fixtures: a class with a fixed key must not be able to see
            // — or clear — the value another class wrote under it.
            await firstDb.StringSetAsync("shared-key", "first");
            (await secondDb.StringGetAsync("shared-key")).HasValue.ShouldBeFalse();
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task ADisposedFixture_LeavesItsDatabaseEmptyForWhoeverGetsItNext()
    {
        var fixture = new RedisFixture();
        await fixture.InitializeAsync();
        var database = fixture.Connection.GetDatabase().Database;
        await fixture.Connection.GetDatabase().StringSetAsync("leftover", "value");
        await fixture.DisposeAsync();

        var next = new RedisFixture();
        await next.InitializeAsync();

        try
        {
            next.Connection.GetDatabase().Database.ShouldBe(database);
            (await next.Connection.GetDatabase().StringGetAsync("leftover")).HasValue.ShouldBeFalse();
        }
        finally
        {
            await next.DisposeAsync();
        }
    }

    [Fact]
    public async Task SearchFixtures_ShareDatabaseZero_WhereTheOnlyMemoryIndexCanLive()
    {
        var first = new MemorySearchFixture();
        var second = new MemorySearchFixture();
        await first.InitializeAsync();
        await second.InitializeAsync();

        try
        {
            second.Endpoint.ShouldBe(first.Endpoint);
            first.Connection.GetDatabase().Database.ShouldBe(0);
            second.Connection.GetDatabase().Database.ShouldBe(0);

            // Created once when the pool started, so parallel classes never race to create it.
            var info = await first.Connection.GetDatabase().FT()
                .InfoAsync(RedisStackMemoryStore.IndexName);
            info.ShouldNotBeNull();
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheTwoVectorWidths_GetSeparateContainers_BecauseTheIndexNameIsAConstant()
    {
        var fabricated = new MemorySearchFixture();
        var real = new LemonadeMemorySearchFixture();
        var keys = new RedisFixture();
        await fabricated.InitializeAsync();
        await real.InitializeAsync();
        await keys.InitializeAsync();

        try
        {
            real.Endpoint.ShouldNotBe(fabricated.Endpoint);
            keys.Endpoint.ShouldNotBe(fabricated.Endpoint);
            keys.Endpoint.ShouldNotBe(real.Endpoint);
        }
        finally
        {
            await fabricated.DisposeAsync();
            await real.DisposeAsync();
            await keys.DisposeAsync();
        }
    }
}