using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using Agent.App;
using Agent.Modules;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
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
using ModelContextProtocol.Server;
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

    public string Address => $"http://localhost:{Port}";

    public TunnelRegistry Registry => _app.Services.GetRequiredService<TunnelRegistry>();

    public IOwnServers OwnServers => _app.Services.GetRequiredService<IOwnServers>();

    // The relay's outposts, which the host runs as it runs the relay: recording each opening and
    // closing on the host's metrics.
    public TunnelledOutposts Outposts => _app.Services.GetRequiredService<TunnelledOutposts>();

    public IOwnChannelServers ChannelServers => _app.Services.GetRequiredService<IOwnChannelServers>();

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
        app.MapConnectedServers(agentCredential);
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
                Tunnel = Quick(hub, credential, serverName)
            }))
            .WithTools<CallTokenGateTests.WhoTools>()
            .WithTools<WaitTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new TunnelledServer(app);
    }

    // Quick to come back, so a test waits on the reconnect rather than the schedule.
    public static TunnelSettings Quick(string hub, string credential, string serverName) => new()
    {
        Hub = hub,
        Credential = credential,
        ServerName = serverName,
        ReconnectAfter = TimeSpan.FromMilliseconds(50),
        ReconnectAtMost = TimeSpan.FromMilliseconds(200)
    };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // A call that stays in flight until its caller gives up, so a test can drop the tunnel under it.
    // Each call names itself, so a test knows its own call has reached the server.
    [McpServerToolType]
    public sealed class WaitTools
    {
        private static readonly ConcurrentDictionary<string, bool> _waiting = new();

        public static bool IsWaiting(string call) => _waiting.ContainsKey(call);

        [McpServerTool(Name = "wait")]
        [Description("Waits until the call is cancelled.")]
        public static async Task<string> Wait(string call, CancellationToken ct)
        {
            _waiting[call] = true;
            await Task.Delay(Timeout.Infinite, ct);
            return call;
        }
    }
}

// A channel server built on the hosting library and told only a hub, a credential and its name, as a
// household's dual-role server is: its notifications are raised through its own emitter, and every
// reply chunk the agent sends back through the tunnel is kept for the test to read.
internal sealed class TunnelledChannelServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TunnelledChannelServer(WebApplication app) => _app = app;

    private sealed record Settings(McpGateSettings Mcp) : IMcpHostSettings;

    public ChannelNotificationEmitter Emitter => _app.Services.GetRequiredService<ChannelNotificationEmitter>();

    public IReadOnlyCollection<string> Replies => _app.Services.GetRequiredService<ReceivedReplies>().Contents;

    public static async Task<TunnelledChannelServer> StartAsync(string hub, string credential, string serverName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<ReceivedReplies>();
        builder.Services
            .AddToolServer(new Settings(new McpGateSettings { Tunnel = TunnelledServer.Quick(hub, credential, serverName) }))
            .AddChannelServer(DeliveryPolicy.Broadcast)
            .WithTools<ReplyTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new TunnelledChannelServer(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed class ReceivedReplies
    {
        private readonly ConcurrentQueue<string> _contents = new();

        public IReadOnlyCollection<string> Contents => _contents;

        public void Add(string content) => _contents.Enqueue(content);
    }

    [McpServerToolType]
    internal sealed class ReplyTools(ReceivedReplies replies)
    {
        [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
        public string SendReply(
            string conversationId, string content, ReplyContentType contentType, bool isComplete, string? messageId,
            string? turnKey = null, bool? agentInitiated = null)
        {
            replies.Add(content);
            return "ok";
        }
    }
}