using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Eval.Fixtures;

// A database per run, not per class.
//
// The scheduling server clears fixed key names on the way in — `schedules`, `schedules:due` and
// every `schedule:*` — because a schedule left behind by an earlier run would count against this
// one's ceiling. That is safe while a class's runs go one at a time and wrong the moment they go
// together: the second run's wipe takes the first run's schedules with it, and the scenario that
// wrote them fails for a reason that is not about the agent. So the leaseholder is the run.
internal sealed class EvalRedis(RedisLease lease) : IAsyncDisposable
{
    // One connection for the whole process, and only ever used to empty a database somebody has
    // finished with: opening one per run would pay a handshake for a FLUSHDB.
    private static readonly Lazy<Task<IConnectionMultiplexer>> _admin = new(async () =>
        await (await RedisPool.GetAsync(RedisPool.KeysPool)).ConnectAsync(0));

    public string ConnectionString => lease.ConnectionString;

    public static async Task<EvalRedis> LeaseAsync()
    {
        var pool = await RedisPool.GetAsync(RedisPool.KeysPool);
        return new EvalRedis(pool.LeaseDatabase());
    }

    public async ValueTask DisposeAsync()
    {
        // Emptied before it goes back rather than after it comes out, so a run never has to
        // wonder whether what it is reading is its own.
        var connection = await _admin.Value;
        await connection.GetServer(connection.GetEndPoints()[0]).FlushDatabaseAsync(lease.Database);
        lease.Return();
    }
}