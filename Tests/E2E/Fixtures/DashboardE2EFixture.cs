using Domain.DTOs.Channel;
using Domain.Security;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Tests.E2E.Fixtures;

public class DashboardE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _observability;
    private WebApplication? _accountsKeys;

    // The dashboard serves only the operator the accounts service's assertion names. There is no
    // proxy or accounts service here, so the browser carries the assertion the session check would
    // have added, signed with the accounts test key, and the dashboard fetches that key's JWKS from
    // this process.
    private static readonly string _operatorAssertion = TestAssertions.Of(
        new Account("e2e-household", "e2e-operator"), AppChannels.Dashboard, ["owner", ForwardedAccount.OperatorRole]);

    protected override IEnumerable<KeyValuePair<string, string>> PageHeaders =>
        [new(ForwardedAccount.AssertionHeader, _operatorAssertion)];

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
        var keysPort = await StartAccountsKeysAsync(ct);

        _observability = TestContainers.Container(observabilityImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("observability")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDIS__CONNECTIONSTRING", $"redis:6379,password={RedisPassword}")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment("ACCOUNTS__JWKSURL", $"http://host.docker.internal:{keysPort}/jwks")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPath("/api/metrics/health")
                    .ForPort(8080)
                    .WithHeader(ForwardedAccount.AssertionHeader, _operatorAssertion)))
            .Build();
        await _observability.StartAsync(ct);

        var host = _observability.Hostname;
        var port = _observability.GetMappedPublicPort(8080);
        DashboardUrl = $"http://{host}:{port}/";

        var redisPort = _redis.GetMappedPublicPort(6379);
        RedisConnectionString = $"{_redis.Hostname}:{redisPort},password={RedisPassword}";
    }

    // The accounts service's published keys, as its internal JWKS route answers them, for the test key.
    private async Task<int> StartAccountsKeysAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.ListenAnyIP(0));
        builder.Logging.ClearProviders();
        _accountsKeys = builder.Build();
        var jwks = TestSigningKeys.Store(TestSigningKeys.Accounts).PublishedKeys.ToJson();
        _accountsKeys.MapGet("/jwks", () => jwks);
        await _accountsKeys.StartAsync(ct);
        return new Uri(_accountsKeys.Urls.First()).Port;
    }

    protected override async Task StopContainersAsync()
    {
        if (_accountsKeys is not null)
        {
            await _accountsKeys.DisposeAsync();
        }

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