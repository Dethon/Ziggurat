using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using Mcp.Hosting.Tunnel;

namespace Infrastructure.Tunnels;

// One opening of one household server's tunnel: a connection generation in ADR 0012's sense. Its
// server, its household and the synthetic address the agent reaches it at stay what they were
// admitted as for as long as it is open; a server that reconnects is a new OpenTunnel with a new
// generation, and nothing carries over from the old one.
//
// The agent reaches the server through Client, an HttpClient whose every connection is a stream of
// this tunnel to the server's /mcp listener — nothing it sends goes onto the network. Each request
// presents the tunnel's credential to the server's /mcp gate, which asks for exactly that, and is
// preceded by the agent's keys whenever they have changed since the server was last handed them.
public sealed class OpenTunnel : IAsyncDisposable
{
    private readonly TunnelMux _mux;
    private readonly SigningKeyStore _keys;
    private readonly SemaphoreSlim _sendingKeys = new(1, 1);
    private readonly SocketsHttpHandler _connections;
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationTokenSource _closed;
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _forwarding = new(StringComparer.Ordinal);
    private string _sentKeys;

    internal OpenTunnel(
        TunnelCredentialVerified credential,
        string presented,
        long generation,
        DateTimeOffset openedAt,
        TunnelMux mux,
        SigningKeyStore keys)
    {
        TenantId = credential.TenantId;
        ServerName = credential.ServerName;
        CredentialId = credential.CredentialId;
        Address = ServerAddress.Tunnelled(credential.TenantId, credential.ServerName);
        Generation = generation;
        OpenedAt = openedAt;
        Presented = presented;
        _mux = mux;
        _keys = keys;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, mux.Closed);
        _sentKeys = keys.PublishedKeys.ToJson();
        _connections = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) => await OpenStreamAsync(TunnelProtocol.McpTag, ct),
            // A stream is not a socket that goes stale on its own: it lasts until either end closes it
            // or the tunnel goes, so the pool keeps it as long as the tunnel lives.
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan
        };
        Client = new HttpClient(new RelayHandler(this, _connections), disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public string TenantId { get; }

    public string ServerName { get; }

    public string CredentialId { get; }

    // What the agent's call tokens for this server are signed for, its tools qualified with and its
    // prompts cached under.
    public ServerAddress Address { get; }

    // Increases with every opening of any tunnel, so two openings of one server are never confused.
    public long Generation { get; }

    public DateTimeOffset OpenedAt { get; }

    public HttpClient Client { get; }

    public bool IsOpen => !_closing.IsCancellationRequested && !_mux.IsClosed;

    // Cancelled once the tunnel is closing or closed, by either end.
    public CancellationToken Closed => _closed.Token;

    // The credential the tunnel was opened with, which its server's /mcp asks for. Held only while the
    // tunnel is open and never written anywhere.
    internal string Presented { get; }

    // What the server is told on open: who it is, the name its tokens are signed for, and the keys.
    internal TunnelAdmitted Admitted => new(TenantId, ServerName, Address.Name, _sentKeys);

    // What TunnelForwarder sends an exchange arriving at the agent through, to the listener this tag
    // names: the mcp listener's presents the credential as Client does, any other presents nothing.
    // Its own pool rather than Client's, set as a reverse proxy's must be — a cookie a response sets
    // is the browser's, never kept here for whoever comes next, and a redirect or a compressed body
    // is passed on as it came.
    internal HttpMessageInvoker Forwarding(string tag) =>
        _forwarding.GetOrAdd(tag, t => new HttpMessageInvoker(
            new RelayHandler(this, new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) => await OpenStreamAsync(t, ct),
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                UseCookies = false,
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ActivityHeadersPropagator = null
            }, presents: t == TunnelProtocol.McpTag),
            disposeHandler: true));

    internal Task<TunnelStream> OpenStreamAsync(string tag, CancellationToken ct) =>
        IsOpen
            ? _mux.OpenAsync(tag, ct)
            : throw new IOException($"the tunnel of {ServerName} closed");

    internal Task SendAsync(TunnelControl control, CancellationToken ct) =>
        _mux.SendControlAsync(JsonSerializer.Serialize(control, TunnelProtocol.SerializerOptions), ct);

    // Read off the store on every exchange: it notices a rotation when its file changes, and a key the
    // store now signs with must reach the server before the call it signed.
    internal async Task SendKeysIfChangedAsync(CancellationToken ct)
    {
        var published = _keys.PublishedKeys.ToJson();
        if (published == Volatile.Read(ref _sentKeys))
        {
            return;
        }

        await _sendingKeys.WaitAsync(ct);
        try
        {
            if (published != _sentKeys)
            {
                await SendAsync(new TunnelKeys(published), ct);
                Volatile.Write(ref _sentKeys, published);
            }
        }
        finally
        {
            _sendingKeys.Release();
        }
    }

    internal async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        if (!_closing.IsCancellationRequested)
        {
            await _closing.CancelAsync();
            await _mux.CloseAsync(status, reason, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        await _mux.DisposeAsync();
        _connections.Dispose();
        foreach (var invoker in _forwarding.Values)
        {
            invoker.Dispose();
        }

        _closed.Dispose();
    }
}