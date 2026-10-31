using System.Net;
using Agent.App;
using Agent.Modules;
using Domain.Contracts;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Infrastructure.Tunnels;
using Mcp.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;

namespace Tests.Integration.Tunnels;

// The relay as the agent hosts it — its registration and its route, over the agent's own signing
// keys, the accounts service it verifies with and the Redis it hears revocations on — on a real port,
// so a server's tunnel crosses a real socket and a restart is a real one.
internal sealed class RelayHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RelayHost(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public string Hub => $"ws://localhost:{Port}{TunnelProtocol.Path}";

    public TunnelRegistry Registry => _app.Services.GetRequiredService<TunnelRegistry>();

    public IOwnServers OwnServers => _app.Services.GetRequiredService<IOwnServers>();

    public static async Task<RelayHost> StartAsync(
        AccountsServer accounts, string agentCredential, string redis, SigningKeyStore keys,
        IMetricsPublisher metrics, int? port = null)
    {
        var listening = port ?? TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, listening));
        builder.Services
            .AddSingleton(keys)
            .AddSingleton(metrics)
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis))
            .AddTunnelRelay(
                new TunnelSettings(),
                new AccountsClientConfig { BaseUrl = accounts.Address.ToString() },
                agentCredential);
        var app = builder.Build();
        app.UseWebSockets();
        app.MapTunnel();
        await app.StartAsync();
        return new RelayHost(app, listening);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

// A server built on the hosting library and told only a hub, a credential and its name: a real host
// on loopback, which opens its tunnel by itself and is reached through it. Its /mcp listener is on
// a port nothing dials.
internal sealed class TunnelledServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TunnelledServer(WebApplication app) => _app = app;

    private sealed record Settings(McpGateSettings Mcp) : IMcpHostSettings;

    public static async Task<TunnelledServer> StartAsync(string hub, string credential, string serverName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services
            .AddToolServer(new Settings(new McpGateSettings
            {
                Tunnel = new TunnelSettings
                {
                    Hub = hub,
                    Credential = credential,
                    ServerName = serverName,
                    // Quick to come back, so a test waits on the reconnect rather than the schedule.
                    ReconnectAfter = TimeSpan.FromMilliseconds(50),
                    ReconnectAtMost = TimeSpan.FromMilliseconds(200)
                }
            }))
            .WithTools<CallTokenGateTests.WhoTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new TunnelledServer(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}