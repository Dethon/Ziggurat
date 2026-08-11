using System.Net;
using Domain.Contracts;
using Domain.DTOs.Channel;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using McpServerScheduling.Modules;
using McpServerScheduling.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpSchedulingServerFixture : IAsyncLifetime
{
    private IContainer _redis = null!;
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // The port, not the log line — see RedisFixture for why the log wait can hang.
        _redis = TestContainers.Container("redis/redis-stack:latest")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();

        await _redis.StartAsync();

        var redisConnection = $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)}";

        var port = TestPort.GetAvailable();
        var settings = new SchedulingSettings
        {
            RedisConnectionString = redisConnection,
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
        await _redis.DisposeAsync();
    }
}