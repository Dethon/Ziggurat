using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients.HomeAssistant;

namespace Tests.Integration.Fixtures;

// Boots a real Home Assistant container with a pre-seeded /config volume (see HomeAssistantSeed) so
// the REST API is reachable without HA's interactive onboarding. HA cold-starts in ~30-60s on a fresh
// /config; the readiness loop polls `/api/` with the bearer token until 200.
public class HomeAssistantFixture : IAsyncLifetime
{
    private IContainer _container = null!;
    private string _configDir = null!;

    public string BaseUrl { get; private set; } = null!;
    public string Token { get; private set; } = null!;
    public const string TestEntityId = HomeAssistantSeed.TestEntityId;

    public async Task InitializeAsync()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"ha-test-{Guid.NewGuid():N}");
        Token = HomeAssistantSeed.WriteConfig(_configDir);

        _container = TestContainers.Container(HomeAssistantSeed.ContainerImage)
            .WithPortBinding(HomeAssistantSeed.Port, true)
            .WithBindMount(_configDir, "/config")
            .WithEnvironment("TZ", "UTC")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(HomeAssistantSeed.Port))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(HomeAssistantSeed.Port);
        BaseUrl = $"http://{host}:{port}";

        await HomeAssistantSeed.WaitForApiReadyAsync(_container, BaseUrl, Token);
    }

    public HomeAssistantClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };
        return new HomeAssistantClient(http, Token);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _container.DisposeAsync();
        }
        finally
        {
            if (_configDir is not null && Directory.Exists(_configDir))
            {
                try
                { Directory.Delete(_configDir, recursive: true); }
                catch { /* best effort — container may still hold handles momentarily */ }
            }
        }
    }
}