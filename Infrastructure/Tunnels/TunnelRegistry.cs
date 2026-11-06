using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.DTOs.Tunnel;

namespace Infrastructure.Tunnels;

// A change to which household servers are connected: a tunnel opened, or one closed and why.
public abstract record TunnelChange(OpenTunnel Tunnel);

public sealed record TunnelOpened(OpenTunnel Tunnel) : TunnelChange(Tunnel);

// Why it closed: a credential refusal's reason (Revoked), Replaced by a newer opening of the same
// server, or the connection's end as its close described it.
public sealed record TunnelClosed(OpenTunnel Tunnel, string Reason) : TunnelChange(Tunnel);

// The tunnels open in this agent, at most one per household server. What the agent reaches a
// household's server through, and what a consumer that follows them (a channel connected while its
// tunnel is open, a turn reconciling its tools) reads: Find for one server now, Open for all of them,
// WatchAsync for every change from here on.
//
// A server opening a tunnel while one of its is still registered replaces it: the old one is closed
// as Replaced and leaves first, so a watcher always sees the old generation go before the new one
// arrives. A reconnecting server whose dead socket had not timed out yet is the ordinary case.
public sealed class TunnelRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string TenantId, string ServerName), OpenTunnel> _open = [];
    private readonly List<Channel<TunnelChange>> _watchers = [];
    private long _generation;

    public IReadOnlyList<OpenTunnel> Open
    {
        get
        {
            lock (_lock)
            {
                return [.. _open.Values];
            }
        }
    }

    // A household's server of that name, while its tunnel is open: one whose socket has closed and
    // that is still on its way out is no tunnel to reach it through, so no caller checks again.
    public OpenTunnel? Find(string tenantId, string serverName)
    {
        lock (_lock)
        {
            return _open.GetValueOrDefault((tenantId, serverName)) is { IsOpen: true } open ? open : null;
        }
    }

    // What the agent's own requests to a listener of one household's server go through: every
    // connection a stream of that household's tunnel of that name under the tag — so a reopened one is
    // used at once and another household's of the same name never is. Nothing is presented: the listener
    // is not the server's /mcp. Its pooled connections belong to the one tunnel they were opened on, and
    // every request asks the registry first: a tunnel taken out of it (revoked, suspended, replaced) still
    // carries bytes until its close reaches the server, and no request may ride it in that moment.
    public HttpMessageHandler StreamsTo(string tenantId, string serverName, string tag) =>
        new CurrentTunnelStreams(() => Find(tenantId, serverName), serverName, tag);

    // Where a request that names no household goes: a browse page's link, a timer's ring. In this phase
    // the only household with servers is the home one, so such a request is for the home household's
    // server of that name — the opening the accounts service called home when it verified its
    // credential (OpenTunnel.Home), as a channel endpoint named by server name is. Another household's
    // server of the same name is never a fallback.
    //
    // It has its edge in plain sight: once a second household runs servers (4b), the request must name
    // its household. The web search server already knows it where a link is minted — web_login's call
    // token carries the person's account — and the timers server where a timer is created.
    public OpenTunnel? FindHome(string serverName)
    {
        lock (_lock)
        {
            return _open.Values.FirstOrDefault(t => t.IsHome(serverName) && t.IsOpen);
        }
    }

    // Every tunnel open when asked, as an opening, then every change as it happens, until the token is
    // cancelled. Nothing is missed between the two: the openings and the subscription are taken together.
    public async IAsyncEnumerable<TunnelChange> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var changes = Channel.CreateUnbounded<TunnelChange>(new UnboundedChannelOptions { SingleReader = true });
        lock (_lock)
        {
            foreach (var tunnel in _open.Values)
            {
                changes.Writer.TryWrite(new TunnelOpened(tunnel));
            }

            _watchers.Add(changes);
        }

        try
        {
            await foreach (var change in changes.Reader.ReadAllAsync(ct))
            {
                yield return change;
            }
        }
        finally
        {
            lock (_lock)
            {
                _watchers.Remove(changes);
            }
        }
    }

    internal long NextGeneration() => Interlocked.Increment(ref _generation);

    // The tunnel this one replaces, if a tunnel of the same server was still registered.
    internal OpenTunnel? Add(OpenTunnel tunnel)
    {
        lock (_lock)
        {
            var key = (tunnel.TenantId, tunnel.ServerName);
            var replaced = _open.GetValueOrDefault(key);
            if (replaced is not null)
            {
                Publish(new TunnelClosed(replaced, TunnelProtocol.Replaced));
            }

            _open[key] = tunnel;
            Publish(new TunnelOpened(tunnel));
            return replaced;
        }
    }

    // Removes this very tunnel, never a newer one of the same server; the first reason given is the one
    // a watcher is told.
    internal void Remove(OpenTunnel tunnel, string reason)
    {
        lock (_lock)
        {
            var key = (tunnel.TenantId, tunnel.ServerName);
            if (_open.GetValueOrDefault(key) == tunnel)
            {
                _open.Remove(key);
                Publish(new TunnelClosed(tunnel, reason));
            }
        }
    }

    internal IReadOnlyList<OpenTunnel> OpenedWith(string credentialId)
    {
        lock (_lock)
        {
            return [.. _open.Values.Where(t => t.CredentialId == credentialId)];
        }
    }

    private void Publish(TunnelChange change) =>
        _watchers.ForEach(watcher => watcher.Writer.TryWrite(change));
}