using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using StackExchange.Redis;

namespace Tests.Integration.Fixtures;

// One Redis per pool for the whole run, rather than one per test class.
//
// Nineteen classes take a Redis fixture and xUnit builds a fixture instance per class, so a full
// run was starting nineteen redis-stack containers. They boot in parallel, so the wall-clock cost
// was small — but each held ~124MB of the RedisInsight-carrying image, and each was more work for a
// daemon the suite is already asking to bring up compose stacks. The server-only image the dev
// stack itself runs holds ~7MB.
//
// A pool is a container plus the rule for handing isolation out on it. Classes that only touch keys
// lease a database of their own: keyspaces are per-database, so fixed key names, KEYS and FLUSHDB
// all stay honest between classes running in parallel. RediSearch is the exception — it indexes
// database 0 and nothing else ("Cannot create index on db != 0") — so classes that search share
// database 0 and are separated by pool instead. One pool per vector width, because the index name
// the memory store uses is a constant and an index has one width.
internal sealed class RedisPool
{
    public const string KeysPool = "keys";

    private const int RedisPort = 6379;

    // Sixteen is the Redis default and the suite already leases more than half of them.
    private const int DatabaseCount = 64;

    private static readonly ConcurrentDictionary<string, Lazy<Task<RedisPool>>> _pools = new();

    private readonly ConcurrentQueue<int> _released = new();
    private int _lastLeased;

    private RedisPool(string endpoint)
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }

    // `prepare` runs once, inside the pool's own initialization, before any fixture sees it. It is
    // captured from whichever caller starts the pool; every caller for a given name passes the same
    // work, because the name is what the work depends on.
    public static Task<RedisPool> GetAsync(string name, Func<RedisPool, Task>? prepare = null) =>
        _pools.GetOrAdd(name, key => new Lazy<Task<RedisPool>>(() => StartAsync(key, prepare))).Value;

    public RedisLease LeaseDatabase()
    {
        if (_released.TryDequeue(out var recycled))
        {
            return new RedisLease(this, recycled, exclusive: true);
        }

        var database = Interlocked.Increment(ref _lastLeased);
        if (database >= DatabaseCount)
        {
            throw new InvalidOperationException(
                $"Redis pool '{Endpoint}' ran out of databases ({DatabaseCount}). Fixtures return "
                + "theirs on dispose, so this means more than that many classes hold one at once.");
        }

        return new RedisLease(this, database, exclusive: true);
    }

    // Database 0 is never leased, so the search pools can hand it to every class that asks.
    public RedisLease ShareSearchDatabase() => new(this, 0, exclusive: false);

    // abortConnect=false keeps the multiplexer retrying instead of throwing if the host-side proxy
    // needs a beat after the in-container ping succeeds. allowAdmin is what lets a fixture FLUSHDB
    // the database it leased; it stays off the string handed to production code.
    public async Task<IConnectionMultiplexer> ConnectAsync(int database) =>
        await ConnectionMultiplexer.ConnectAsync(
            $"{ConnectionStringFor(database)},abortConnect=false,allowAdmin=true");

    public string ConnectionStringFor(int database) => $"{Endpoint},defaultDatabase={database}";

    public void Return(int database) => _released.Enqueue(database);

    private static async Task<RedisPool> StartAsync(string name, Func<RedisPool, Task>? prepare)
    {
        // Readiness is a PING answered, not the log line and not the port alone. The log wait can
        // start polling after "Ready to accept connections" was already written and hang forever,
        // and the external port is answered by Docker's proxy before Redis inside is serving, which
        // made connecting flaky. The port wait still guards the mapped-port lookup; the ping proves
        // Redis is up.
        var container = TestContainers.Container("redis/redis-stack-server:latest", $"redis-{name}")
            .WithPortBinding(RedisPort, true)
            .WithEnvironment("REDIS_ARGS", $"--databases {DatabaseCount}")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilExternalTcpPortIsAvailable(RedisPort)
                .UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        // The shared resource reaper starts lazily on the first container of the run, and its own
        // startup can time out while the Docker daemon is busy bringing up an E2E stack. That
        // failure now poisons every class on this pool, not just one, so ride it out with a retry.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await container.StartAsync();
                break;
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        // Nothing disposes the container: it outlives every fixture that borrows from it, and the
        // Testcontainers resource reaper removes it when the test process exits.
        var pool = new RedisPool($"{container.Hostname}:{container.GetMappedPublicPort(RedisPort)}");

        if (prepare is not null)
        {
            await prepare(pool);
        }

        return pool;
    }
}

// Public only because the fixtures are: the pool itself stays inside the fixture code.
public sealed class RedisLease
{
    private readonly RedisPool _pool;

    internal RedisLease(RedisPool pool, int database, bool exclusive)
    {
        _pool = pool;
        Database = database;
        Exclusive = exclusive;
    }

    public int Database { get; }

    // An exclusive database is the leaseholder's to clear; a shared one holds other classes' data.
    public bool Exclusive { get; }

    public string Endpoint => _pool.Endpoint;

    public string ConnectionString => _pool.ConnectionStringFor(Database);

    public Task<IConnectionMultiplexer> ConnectAsync() => _pool.ConnectAsync(Database);

    public void Return()
    {
        if (Exclusive)
        {
            _pool.Return(Database);
        }
    }
}