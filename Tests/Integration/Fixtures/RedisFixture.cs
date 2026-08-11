using StackExchange.Redis;

namespace Tests.Integration.Fixtures;

// A Redis of one's own, without a container of one's own: the class gets a database on the shared
// pool that nothing else writes to, and hands it back empty. See RedisPool for why.
//
// Classes that build a RedisStackMemoryStore cannot use this one — its index only exists on
// database 0. They take MemorySearchFixture instead.
public class RedisFixture : IAsyncLifetime
{
    private RedisLease _lease = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    // Carries the leased database, so a service configured from this string lands where the test
    // looks for it rather than on database 0.
    public string ConnectionString { get; private set; } = null!;

    public string Endpoint => _lease.Endpoint;

    public async Task InitializeAsync()
    {
        _lease = await AcquireAsync();
        ConnectionString = _lease.ConnectionString;
        Connection = await _lease.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_lease.Exclusive)
        {
            var server = Connection.GetServer(Connection.GetEndPoints()[0]);
            await server.FlushDatabaseAsync(_lease.Database);
        }

        await Connection.DisposeAsync();
        _lease.Return();
    }

    protected virtual async Task<RedisLease> AcquireAsync()
    {
        var pool = await RedisPool.GetAsync(RedisPool.KeysPool);
        return pool.LeaseDatabase();
    }
}