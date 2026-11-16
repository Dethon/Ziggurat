namespace Infrastructure.Tunnels;

// A transport onto whichever tunnel of one household's server is open now: a pool of connections per
// tunnel, each a stream of it under the tag, and a request sent only on the pool of the tunnel the
// registry answers at that moment. The pool of a tunnel the registry no longer answers is let go, so a
// connection kept from it is never reused.
internal sealed class CurrentTunnelStreams(Func<OpenTunnel?> current, string serverName, string tag) : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private (OpenTunnel Tunnel, HttpMessageInvoker Pool)? _pooled;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        PoolFor(current() ?? throw new HttpRequestException($"{serverName} is offline: its tunnel is not open"))
            .SendAsync(request, ct);

    private HttpMessageInvoker PoolFor(OpenTunnel tunnel)
    {
        HttpMessageInvoker? stale = null;
        HttpMessageInvoker pool;
        lock (_lock)
        {
            if (_pooled is { } kept && ReferenceEquals(kept.Tunnel, tunnel))
            {
                return kept.Pool;
            }

            stale = _pooled?.Pool;
            pool = new HttpMessageInvoker(new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) => await tunnel.OpenStreamAsync(tag, ct),
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                UseCookies = false,
                UseProxy = false,
                AllowAutoRedirect = false,
                ActivityHeadersPropagator = null
            }, disposeHandler: true);
            _pooled = (tunnel, pool);
        }

        // A request still on the old tunnel's pool ends with that tunnel anyway.
        stale?.Dispose();
        return pool;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _pooled?.Pool.Dispose();
                _pooled = null;
            }
        }

        base.Dispose(disposing);
    }
}