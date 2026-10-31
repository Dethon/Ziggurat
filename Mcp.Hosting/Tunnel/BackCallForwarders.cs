using System.Net;
using System.Net.Sockets;
using Domain.DTOs.Tunnel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mcp.Hosting.Tunnel;

// The tunnel this server's back-calls go out on: the one the relay last admitted, while it is open.
// TunnelClient says when one is admitted and when it ends, so a back-call made between two openings
// fails at once instead of waiting for the next.
internal sealed class TunnelLink
{
    private TunnelMux? _admitted;

    public bool IsAdmitted => Volatile.Read(ref _admitted) is { IsClosed: false };

    public void Admitted(TunnelMux mux) => Volatile.Write(ref _admitted, mux);

    public void Ended(TunnelMux mux) => Interlocked.CompareExchange(ref _admitted, null, mux);

    public Task<TunnelStream> OpenAsync(string tag, CancellationToken ct) =>
        Volatile.Read(ref _admitted) is { IsClosed: false } mux
            ? mux.OpenAsync(tag, ct)
            : throw new IOException("this server's tunnel is not open");
}

// Every back-call this server is configured to make (TunnelSettings.BackCalls), each a loopback port
// whose every connection becomes a stream of the tunnel under that back-call's tag, carrying the
// client's bytes as they are. The relay at the other end decides what the stream may ask and says
// who asked; nothing here knows what travels inside, so a client of any kind — .NET's or the
// sandbox's FUSE daemon — is pointed at its port and changes nothing else.
//
// With no tunnel open, a connection is closed as soon as it is taken: the client's request fails
// rather than hanging until the tunnel comes back.
internal sealed class BackCallForwarders(TunnelSettings settings, TunnelLink link, ILogger<BackCallForwarders> logger)
    : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<TcpListener> _listeners = [];
    private Task _accepting = Task.CompletedTask;

    // Bound before the host counts as started, so a port another process holds fails the server's
    // start instead of every back-call later.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var accepting = settings.BackCalls.Select(backCall =>
        {
            var listener = new TcpListener(IPAddress.Loopback, backCall.Value);
            listener.Start();
            _listeners.Add(listener);
            return AcceptAsync(backCall.Key.ToLowerInvariant(), listener, _stopping.Token);
        }).ToList();
        _accepting = Task.WhenAll(accepting);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        _listeners.ForEach(l => l.Stop());
        await _accepting.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task AcceptAsync(string tag, TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = ForwardAsync(tag, connection, ct);
        }
    }

    private async Task ForwardAsync(string tag, TcpClient connection, CancellationToken ct)
    {
        using (connection)
        {
            TunnelStream stream;
            try
            {
                stream = await link.OpenAsync(tag, ct);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                logger.LogWarning("A '{Tag}' back-call could not go out: {Reason}", tag, ex.Message);
                return;
            }

            await using (stream)
            await using (var network = connection.GetStream())
            {
                try
                {
                    await Task.WhenAny(stream.CopyToAsync(network, ct), network.CopyToAsync(stream, ct));
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
                {
                }
            }
        }
    }
}