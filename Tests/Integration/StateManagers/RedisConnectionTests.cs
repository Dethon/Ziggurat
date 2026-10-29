using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.StateManagers;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Integration.StateManagers;

// The resilient connect tolerates a Redis that is slow to come up, which once meant a wrong
// password retried in the background forever and the server came up looking healthy with no
// store. A refusal of the password is not slowness, so it ends startup and says why.
public class RedisConnectionTests : IAsyncLifetime
{
    private const string Password = "the-deployment-password";
    private IContainer _redis = null!;

    public async Task InitializeAsync()
    {
        _redis = TestContainers.Container("redis/redis-stack-server:latest")
            .WithEnvironment("REDIS_ARGS", $"--requirepass {Password}")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync();
    }

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    private string Endpoint => $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}";

    [Fact]
    public async Task ConnectResiliently_WithThePassword_Connects()
    {
        using var connection = RedisConnection.ConnectResiliently($"{Endpoint},password={Password}");

        (await connection.GetDatabase().PingAsync()).ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void ConnectResiliently_WithAWrongPassword_FailsNamingRedisAuthentication()
    {
        var thrown = Should.Throw<RedisConnectionException>(() =>
            RedisConnection.ConnectResiliently($"{Endpoint},password=not-it"));

        thrown.Message.ShouldContain("authentication", Case.Insensitive);
    }
}