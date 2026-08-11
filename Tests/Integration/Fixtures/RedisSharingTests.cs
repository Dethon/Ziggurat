using Infrastructure.Memory;
using NRedisStack.RedisStackCommands;
using Shouldly;
using StackExchange.Redis;

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
        // Read back through a connection of our own rather than through the next lease: the
        // database goes back into the pool on dispose, and under a full run some other class
        // is free to take it before this test asks for one.
        var fixture = new RedisFixture();
        await fixture.InitializeAsync();
        var connectionString = fixture.ConnectionString;
        var key = $"leftover-{Guid.NewGuid():N}";

        await fixture.Connection.GetDatabase().StringSetAsync(key, "value");
        await fixture.DisposeAsync();

        await using var probe = await ConnectionMultiplexer.ConnectAsync($"{connectionString},abortConnect=false");
        (await probe.GetDatabase().StringGetAsync(key)).HasValue.ShouldBeFalse();
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