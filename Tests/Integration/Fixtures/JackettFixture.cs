using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients.Torrent;

namespace Tests.Integration.Fixtures;

public class JackettFixture : IAsyncLifetime
{
    private const int JackettPort = 9117;
    private const string TestApiKey = "integrationtestapikey123";

    private IContainer _container = null!;
    private string? _configDir;

    private string ApiUrl { get; set; } = null!;
    private static string ApiKey => TestApiKey;

    public async Task InitializeAsync()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"jackett-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_configDir);

        var serverConfig = $$"""
                             {
                               "Port": {{JackettPort}},
                               "AllowExternal": true,
                               "APIKey": "{{TestApiKey}}",
                               "AdminPassword": null,
                               "CacheEnabled": true,
                               "CacheTtl": 2100,
                               "CacheMaxResultsPerIndexer": 1000
                             }
                             """;
        await File.WriteAllTextAsync(Path.Combine(_configDir, "ServerConfig.json"), serverConfig);

        _container = TestContainers.Container("lscr.io/linuxserver/jackett:0.24.306")
            .WithPortBinding(JackettPort, true)
            .WithEnvironment("PUID", "1000")
            .WithEnvironment("PGID", "1000")
            .WithEnvironment("TZ", "UTC")
            .WithBindMount(_configDir, "/config/Jackett")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilExternalTcpPortIsAvailable(JackettPort))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(JackettPort);
        ApiUrl = $"http://{host}:{port}/api/v2.0/";

        await WaitForApiReady();
    }

    private async Task WaitForApiReady()
    {
        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(ApiUrl);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        for (var i = 0; i < 30; i++)
        {
            try
            {
                var response = await httpClient.GetAsync("server/config");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // Ignore and retry
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException("Jackett API did not become ready in time");
    }

    public JackettSearchClient CreateClient()
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(ApiUrl)
        };
        return new JackettSearchClient(httpClient, ApiKey);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        if (_configDir != null && Directory.Exists(_configDir))
        {
            try
            { Directory.Delete(_configDir, true); }
            catch
            {
                /* ignore */
            }
        }
    }
}