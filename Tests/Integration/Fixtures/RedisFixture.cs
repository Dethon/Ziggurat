using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;

namespace Tests.Integration.Fixtures;

public class RedisFixture : IAsyncLifetime
{
    private const int RedisPort = 6379;
    private IContainer _container = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Readiness is a PING answered, not the log line and not the port alone. The log
        // wait can start polling after "Ready to accept connections" was already written
        // and hang forever, and the external port is answered by Docker's proxy before
        // Redis inside is serving, which made the ConnectAsync below flaky. The port wait
        // still guards the mapped-port lookup; the ping proves Redis is up.
        _container = new ContainerBuilder("redis/redis-stack:latest")
            .WithPortBinding(RedisPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilExternalTcpPortIsAvailable(RedisPort)
                .UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        // The shared resource reaper starts lazily on the first container of the run, and its
        // own startup can time out while the Docker daemon is busy bringing up an E2E stack.
        // That failure poisons every test on this fixture, so ride it out with a second try.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _container.StartAsync();
                break;
            }
            catch (InvalidOperationException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(RedisPort);
        ConnectionString = $"{host}:{port}";

        // abortConnect=false keeps the multiplexer retrying instead of throwing if the
        // host-side proxy needs a beat after the in-container ping succeeds.
        Connection = await ConnectionMultiplexer.ConnectAsync($"{ConnectionString},abortConnect=false");
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}