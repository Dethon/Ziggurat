using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private bool _keepsPort;

    private RelayHost(WebApplication app, int port, VfsBridge bridge, ConcurrentQueue<ReachedTarget> reached, bool secure)
    {
        _app = app;
        Port = port;
        Bridge = bridge;
        Reached = reached;
        _scheme = secure ? "wss" : "ws";
    }

    private readonly string _scheme;

    public int Port { get; }

    public VfsBridge Bridge { get; }

    // Every request the stand-in targets answered, with the headers that reached them.
    public ConcurrentQueue<ReachedTarget> Reached { get; }

    public string Hub => $"{_scheme}://localhost:{Port}{TunnelProtocol.Path}";

    // Where the proxy sends what it routes to the agent.
    public string Address => $"http://localhost:{Port}";

    // The same, for a server in a container the suite started, listening on every address.
    public string HubFromContainers => $"ws://host.docker.internal:{Port}{TunnelProtocol.Path}";

    public TunnelRegistry Registry => _app.Services.GetRequiredService<TunnelRegistry>();

    public IOwnServers OwnServers => _app.Services.GetRequiredService<IOwnServers>();

    // The relay's outposts, which the host runs as it runs the relay: recording each opening and
    // closing on the host's metrics.
    public TunnelledOutposts Outposts => _app.Services.GetRequiredService<TunnelledOutposts>();

    public IOwnChannelServers ChannelServers => _app.Services.GetRequiredService<IOwnChannelServers>();

    public static async Task<RelayHost> StartAsync(
        AccountsServer accounts, string agentCredential, string redis, SigningKeyStore keys,
        IMetricsPublisher metrics, int? port = null, IPAddress? on = null,
        IReadOnlyDictionary<string, string>? sharedServers = null,
        // Served over TLS with this certificate and its chain, as the proxy serves the tunnel's route:
        // a hub the servers reach at wss://.
        HubCertificate? certificate = null,
        // Where the relay's logs go besides the console, for a test reading what an operator would see.
        ILoggerProvider? logs = null)
    {
        // Picked ahead rather than bound on port 0: the shared Lemonade's stand-in is this host itself,
        // named in the back-call settings before it listens.
        var listening = port ?? TestPort.GetAvailable();
        var bridge = new VfsBridge(TimeProvider.System, new VfsBridgeSettings());
        var reached = new ConcurrentQueue<ReachedTarget>();
        var builder = WebApplication.CreateBuilder();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.WebHost.UseKestrel(options => options.Listen(on ?? IPAddress.Loopback, listening, listen =>
        {
            if (certificate is not null)
            {
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate.Leaf,
                    ServerCertificateChain = certificate.Chain
                });
            }
        }));
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
        return new RelayHost(app, listening, bridge, reached, certificate is not null);
    }

    // A restart must come back on the same port, so this stop keeps the number out of the pool for
    // the relay started on it next, which gives it back when it is disposed.
    public async Task<int> StopKeepingItsPortAsync()
    {
        _keepsPort = true;
        await DisposeAsync();
        return Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (!_keepsPort)
        {
            TestPort.Release(Port);
        }
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

    // `backCalls` are the back-calls the server makes, each at the loopback port given;
    // `hubAuthority` a certificate authority's PEM file the hub's certificate may chain to.
    // `delegations`, a store it keeps delegations in, as Home Assistant's server keeps its watches'.
    public static async Task<TunnelledServer> StartAsync(
        string hub, string credential, string serverName, Dictionary<string, int>? backCalls = null,
        string hubAuthority = "", IDelegationStore? delegations = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var server = builder.Services
            .AddToolServer(new Settings(new McpGateSettings
            {
                Tunnel = Quick(hub, credential, serverName) with
                {
                    BackCalls = backCalls ?? [],
                    HubCertificateAuthority = hubAuthority
                }
            }))
            .WithTools<CallTokenGateTests.WhoTools>()
            .WithTools<WaitTools>();
        if (delegations is not null)
        {
            builder.Services.AddSingleton(delegations);
            server.WithTools<DelegationStoreTools>();
        }

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
// A certificate authority of a test's own, shaped as a local stack's proxy has one (Caddy's `tls
// internal`): a root, written as a PEM file a server can be told of, and an intermediate under it that
// issues each host's certificate, which the hub serves with the intermediate beside it. The root alone
// is what a server is told of, so a check that builds the chain from the leaf and the root alone fails
// here as it fails against Caddy.
internal sealed class LocalAuthority : IDisposable
{
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;

    private LocalAuthority(X509Certificate2 root, X509Certificate2 intermediate, string file)
    {
        _root = root;
        _intermediate = intermediate;
        CertificateFile = file;
    }

    public string CertificateFile { get; }

    public static LocalAuthority Create()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = AuthorityRequest("CN=Ziggurat Test Local Authority Root", rootKey, pathLength: 1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = AuthorityRequest("CN=Ziggurat Test Local Authority Intermediate", intermediateKey, pathLength: 0);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        using var signed = request.Create(
            root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(16));
        var intermediate = signed.CopyWithPrivateKey(intermediateKey);
        var file = Path.Combine(Path.GetTempPath(), $"local-authority-{Guid.NewGuid():N}.crt");
        File.WriteAllText(file, root.ExportCertificatePem());
        return new LocalAuthority(root, intermediate, file);
    }

    private static CertificateRequest AuthorityRequest(string name, ECDsa key, int pathLength)
    {
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, pathLength, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign
            | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    // A server certificate for `host`, signed by this authority's intermediate, with its private key
    // and the chain the hub serves beside it.
    public HubCertificate Issue(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={host}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_intermediate, true, false));
        using var issued = request.Create(
            _intermediate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(28),
            RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        // Through PKCS#12, so the key is one Kestrel's TLS can use on every platform.
        return new HubCertificate(
            X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null),
            [X509CertificateLoader.LoadCertificate(_intermediate.RawData)]);
    }

    public void Dispose()
    {
        _root.Dispose();
        _intermediate.Dispose();
        File.Delete(CertificateFile);
    }
}

// What a hub serves: its own certificate, and the intermediates between it and the root.
internal sealed record HubCertificate(X509Certificate2 Leaf, X509Certificate2Collection Chain) : IDisposable
{
    public void Dispose()
    {
        Leaf.Dispose();
        Chain.ToList().ForEach(c => c.Dispose());
    }
}