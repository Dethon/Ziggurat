using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Tests.E2E.Fixtures;

public class DashboardE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _observability;

    public string DashboardUrl { get; private set; } = "";
    private const string RedisPassword = "e2e-dashboard";

    public string RedisConnectionString { get; private set; } = "";

    protected override async Task BuildImagesAsync(CancellationToken ct)
    {
        var solutionRoot = TestHelpers.FindSolutionRoot();

        // observability:latest is FROM base-sdk:latest, so the base has to land first.
        await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, ct);
        await TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.Observability, ct);
    }

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        _network = TestContainers.Network()
            .WithName($"e2e-dashboard-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // Behind a password, as the deployment's Redis is: this is the one stack that exercises the
        // connection string carrying it, so a service that dropped it would fail here.
        _redis = TestContainers.Container("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithEnvironment("REDIS_ARGS", $"--requirepass {RedisPassword}")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        var observabilityImageName = E2EImages.Observability.ImageName;

        _observability = TestContainers.Container(observabilityImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("observability")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDIS__CONNECTIONSTRING", $"redis:6379,password={RedisPassword}")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/api/metrics/health")))
            .Build();
        await _observability.StartAsync(ct);

        var host = _observability.Hostname;
        var port = _observability.GetMappedPublicPort(8080);
        DashboardUrl = $"http://{host}:{port}/";

        var redisPort = _redis.GetMappedPublicPort(6379);
        RedisConnectionString = $"{_redis.Hostname}:{redisPort},password={RedisPassword}";
    }

    protected override async Task StopContainersAsync()
    {
        if (_observability is not null)
        {
            await _observability.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}

[CollectionDefinition("DashboardE2E")]
public class DashboardE2ECollection : ICollectionFixture<DashboardE2EFixture>;