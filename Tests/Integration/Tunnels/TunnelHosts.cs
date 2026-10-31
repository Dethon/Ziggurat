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
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Infrastructure.Tunnels;
using Mcp.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using StackExchange.Redis;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;

namespace Tests.Integration.Tunnels;

// The relay as the agent hosts it — its registration and its route, over the agent's own signing
// keys, the accounts service it verifies with and the Redis it hears revocations on — on a real port,
// so a server's tunnel crosses a real socket and a restart is a real one.
//
// Beside the relay, what its back-calls reach: the agent's real exec bridge (over Bridge, which a
// test mints calls on), and stand-ins for topic minting and the shared Lemonade that record what
// reached them, since the gateway asks the agent's own routes where the agent listens and Lemonade
// is pointed here too.
internal sealed class RelayHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RelayHost(WebApplication app, int port, VfsBridge bridge, ConcurrentQueue<ReachedTarget> reached)
    {
        _app = app;
        Port = port;
        Bridge = bridge;
        Reached = reached;
    }

    public int Port { get; }

    public VfsBridge Bridge { get; }

    // Every request the stand-in targets answered, with the headers that reached them.
    public ConcurrentQueue<ReachedTarget> Reached { get; }

    public string Hub => $"ws://localhost:{Port}{TunnelProtocol.Path}";

    // Where the proxy sends what it routes to the agent.
    public string Address => $"http://localhost:{Port}";

    // The same, for a server in a container the suite started, listening on every address.
    public string HubFromContainers => $"ws://host.docker.internal:{Port}{TunnelProtocol.Path}";

    public TunnelRegistry Registry => _app.Services.GetRequiredService<TunnelRegistry>();

    public IOwnServers OwnServers => _app.Services.GetRequiredService<IOwnServers>();

    public IOwnChannelServers ChannelServers => _app.Services.GetRequiredService<IOwnChannelServers>();

    public static async Task<RelayHost> StartAsync(
        AccountsServer accounts, string agentCredential, string redis, SigningKeyStore keys,
        IMetricsPublisher metrics, int? port = null, IPAddress? on = null,
        IReadOnlyDictionary<string, string>? sharedServers = null)
    {
        // Picked ahead rather than bound on port 0: the shared Lemonade's stand-in is this host itself,
        // named in the back-call settings before it listens.
        var listening = port ?? TestPort.GetAvailable();
        var bridge = new VfsBridge(TimeProvider.System, new VfsBridgeSettings());
        var reached = new ConcurrentQueue<ReachedTarget>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(on ?? IPAddress.Loopback, listening));
        builder.Services
            .AddSingleton(keys)
            .AddSingleton(metrics)
            .AddSingleton<IVfsBridge>(bridge)
            // The deployment's own servers' credentials, as the agent holds them to dial each: the ones
            // a shared server presents to the relay's endpoint.
            .AddSingleton(new McpCredentials(sharedServers ?? new Dictionary<string, string>()))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis))
            .AddTunnelRelay(
                new TunnelSettings(),
                new AccountsClientConfig { BaseUrl = accounts.Address.ToString() },
                agentCredential,
                new BackCallSettings { LemonadeUrl = $"http://127.0.0.1:{listening}" });
        var app = builder.Build();
        app.UseWebSockets();
        app.MapTunnel();
        app.MapConnectedServers(agentCredential);
        app.MapBrowsePages();
        app.MapRelayedCalls();
        app.MapVfsBridge();
        app.MapPost(TopicMinting.Path, (HttpContext http) =>
        {
            reached.Enqueue(ReachedTarget.Of(http));
            return Results.Json(new TopicMinted("topic-1", "conversation-1"));
        });
        app.MapPost("/v1/audio/{verb}", (HttpContext http) =>
        {
            reached.Enqueue(ReachedTarget.Of(http));
            return Results.Text("spoken");
        });
        await app.StartAsync();
        return new RelayHost(app, listening, bridge, reached);
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

    // Whether the relay has admitted its tunnel as far as the server knows: its back-calls go out.
    public bool IsAdmitted => _app.Services.GetRequiredService<Mcp.Hosting.Tunnel.TunnelLink>().IsAdmitted;

    internal sealed record Settings(McpGateSettings Mcp) : IMcpHostSettings;

    // `backCalls` are the back-calls the server makes, each at the loopback port given.
    public static async Task<TunnelledServer> StartAsync(
        string hub, string credential, string serverName, Dictionary<string, int>? backCalls = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services
            .AddToolServer(new Settings(new McpGateSettings
            {
                Tunnel = Quick(hub, credential, serverName) with { BackCalls = backCalls ?? [] }
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

// A request one of the relay host's stand-in targets answered: where it went and who it said asked.
internal sealed record ReachedTarget(string Path, string? Household, string? Server, string? Authorization)
{
    public static ReachedTarget Of(HttpContext http) => new(
        http.Request.Path.Value ?? "",
        http.Request.Headers[BackCallProtocol.HouseholdHeader].SingleOrDefault(),
        http.Request.Headers[BackCallProtocol.ServerHeader].SingleOrDefault(),
        http.Request.Headers.Authorization.SingleOrDefault());
}