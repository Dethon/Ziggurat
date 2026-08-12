using System.Net;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpServerScheduling.Modules;
using McpServerScheduling.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpSchedulingServerFixture : IAsyncLifetime
{
    private RedisLease _redis = null!;
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // The scheduling server only stores keys, so it takes a database on the shared pool like
        // any other class — the connection string carries it.
        _redis = (await RedisPool.GetAsync(RedisPool.KeysPool)).LeaseDatabase();

        var port = TestPort.GetAvailable();
        var settings = new SchedulingSettings
        {
            RedisConnectionString = _redis.ConnectionString,
            DispatchIntervalSeconds = 3600,
            DefaultDeliverTo = ["signalr"]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureScheduling(settings);

        var app = builder.Build();
        app.MapMcp("/mcp");

        app.Services.GetRequiredService<IMutableAgentCatalog>()
            .Replace([new AgentCatalogEntry("jonas", "Jonas", "test agent")]);

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        await using var connection = await _redis.ConnectAsync();
        await connection.GetServer(connection.GetEndPoints()[0]).FlushDatabaseAsync(_redis.Database);
        _redis.Return();
    }
}