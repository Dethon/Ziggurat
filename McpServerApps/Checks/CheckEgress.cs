using System.Net;
using System.Net.Sockets;
using System.Text;
using McpServerApps.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerApps.Checks;

// The check browser's one way out. In the deployment its network (`apps-browser`) is internal — no route
// anywhere — so everything but the app origins (which it reaches at the pages listener directly) goes
// through this proxy (CheckBrowser: `--proxy-server`, loopback included). A page's code chooses where its
// requests go, and the check's route judges only a request's first address, so an origin the app may reach
// can redirect the browser on: this judges every address the browser dials, after resolving it, and
// reaches the public internet alone (EgressAddresses), never the LAN, the Docker host or the deployment's
// own network, whose services this server sits beside.
//
// - **CONNECT alone**: everything a check page reaches through it is https (the CDNs, an app's `connect`
//   origins); plain http, which would be a request to forward rather than a tunnel, is refused.
// - **Resolved once, then dialled by address**: every address a name resolves to must be public, and the
//   connection is made to one of those very addresses, never by the name again, so a name that answers
//   differently the second time (DNS rebinding) reaches nothing it was not judged on.
// - **Bound where the browser reaches it alone**: on this machine's address on the network shared with the
//   browser server (Checks:Browser:Endpoint's host), so no other container on the deployment's network
//   gets a way out through it; on loopback when the browser is launched here.
// - No HTTP pipeline: a raw listener of its own, outside the MCP secret's gate and the pages listener.
public sealed class CheckEgress(AppsSettings settings, IEgressNetwork network, ILogger<CheckEgress> logger) : BackgroundService
{
    private const int MaxHeadBytes = 8 * 1024;
    private const int MaxConnections = 256;
    private static readonly TimeSpan _headDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _dialDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _bindRetry = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);
    private readonly TaskCompletionSource<IPEndPoint> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Where it listens, once it does.
    public Task<IPEndPoint> Listening => _listening.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new TcpListener(await BindAddressAsync(stoppingToken), settings.Listeners.Egress);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            // Whoever waits on the listener hears why it never came, rather than waiting out its own deadline.
            _listening.TrySetException(ex);
            throw;
        }

        _listening.TrySetResult((IPEndPoint)listener.LocalEndpoint);
        logger.LogInformation("The check browser's egress listens on {Endpoint}", listener.LocalEndpoint);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                if (!_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        {
                            await TunnelAsync(client, stoppingToken);
                        }
                    }
                    finally
                    {
                        _connections.Release();
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // This machine's address facing the browser server, found by asking the kernel which local address it
    // would send from (a UDP socket's connect sends nothing). Retried until the browser server's name
    // resolves: compose starts it first, but a restart may race.
    private async Task<IPAddress> BindAddressAsync(CancellationToken ct)
    {
        if (settings.Checks.Browser.Endpoint is "")
        {
            return IPAddress.Loopback;
        }

        var host = new Uri(settings.Checks.Browser.Endpoint).Host;
        while (true)
        {
            try
            {
                var browser = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).First();
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(browser, 9);
                return ((IPEndPoint)probe.LocalEndPoint!).Address;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                logger.LogWarning(ex, "The check browser {Host} cannot be found yet; its egress waits", host);
                await Task.Delay(_bindRetry, ct);
            }
        }
    }

    private async Task TunnelAsync(TcpClient client, CancellationToken stopping)
    {
        var browser = client.GetStream();
        byte[] head;
        int length;
        using (var reading = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            reading.CancelAfter(_headDeadline);
            try
            {
                (head, length) = await HeadAsync(browser, reading.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                return;
            }
        }

        var end = HeadEnd(head, length);
        if (end < 0)
        {
            await AnswerAsync(browser, 400, "Bad Request", "A request head ends with an empty line, within 8 KiB.");
            return;
        }

        var line = Encoding.ASCII.GetString(head, 0, Array.IndexOf(head, (byte)'\r', 0, end)).Split(' ');
        if (line is not ["CONNECT", var target, ['H', 'T', 'T', 'P', '/', ..]] || Target(target) is not var (host, port))
        {
            await AnswerAsync(browser, 405, "Method Not Allowed", "This egress tunnels https alone: CONNECT host:port.");
            return;
        }

        IReadOnlyList<IPAddress> addresses;
        using (var resolving = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            resolving.CancelAfter(_dialDeadline);
            try
            {
                addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await network.ResolveAsync(host, resolving.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !stopping.IsCancellationRequested)
            {
                await AnswerAsync(browser, 502, "Bad Gateway", $"{host} does not resolve.");
                return;
            }
        }

        // Every address the name answers with is judged, so one public answer cannot carry a private one.
        if (addresses.Count == 0 || addresses.Any(a => !EgressAddresses.IsPublic(a)))
        {
            logger.LogInformation("Refused the check browser {Host}:{Port}: it resolves to {Addresses}, not all public",
                host, port, string.Join(", ", addresses));
            await AnswerAsync(browser, 403, "Forbidden", $"{host} is no public address: a check reaches the public internet alone.");
            return;
        }

        if (await DialAsync(addresses, port, stopping) is not { } upstream)
        {
            await AnswerAsync(browser, 502, "Bad Gateway", $"{host}:{port} did not answer.");
            return;
        }

        await using (upstream)
        {
            await browser.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stopping);
            if (length > end)
            {
                await upstream.WriteAsync(head.AsMemory(end, length - end), stopping);
            }

            using var closing = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            await Task.WhenAny(
                PipeAsync(browser, upstream, closing.Token),
                PipeAsync(upstream, browser, closing.Token));
            await closing.CancelAsync();
        }
    }

    // Each judged address in turn, never the name: what was judged is what is dialled.
    private async Task<Stream?> DialAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken stopping)
    {
        foreach (var address in addresses)
        {
            using var dialling = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            dialling.CancelAfter(_dialDeadline);
            try
            {
                return await network.DialAsync(new IPEndPoint(address, port), dialling.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !stopping.IsCancellationRequested)
            {
                logger.LogDebug(ex, "The check browser's egress could not reach {Address}:{Port}", address, port);
            }
        }

        return null;
    }

    private static async Task PipeAsync(Stream from, Stream to, CancellationToken ct)
    {
        try
        {
            await from.CopyToAsync(to, ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Either side gone ends the tunnel.
        }
    }

    // Bytes up to the head's empty line, and whatever came after it in the same reads.
    private static async Task<(byte[] Head, int Length)> HeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length && HeadEnd(buffer, length) < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return (buffer, length);
    }

    // Past the head's closing CRLFCRLF, or -1.
    private static int HeadEnd(byte[] buffer, int length)
    {
        var at = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        return at < 0 ? -1 : at + 4;
    }

    // `host:port`, the host a name or an IPv4 literal, or `[IPv6]:port`.
    private static (string Host, int Port)? Target(string target)
    {
        var colon = target.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            return null;
        }

        var host = target[..colon];
        host = host is ['[', .., ']'] ? host[1..^1] : host;
        return host.Length > 0 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')
            ? (host, port)
            : null;
    }

    private static async Task AnswerAsync(Stream browser, int status, string reason, string why)
    {
        var body = Encoding.UTF8.GetBytes(why);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        try
        {
            await browser.WriteAsync(head);
            await browser.WriteAsync(body);
        }
        catch (IOException)
        {
            // The browser left first.
        }
    }

    public override void Dispose()
    {
        _connections.Dispose();
        base.Dispose();
    }
}

// The network the egress resolves and dials on: the machine's own in the deployment, one a test arranges in
// the egress's own tests. The judgment of an address (EgressAddresses) sits above it and never changes.
public interface IEgressNetwork
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);

    Task<Stream> DialAsync(IPEndPoint endpoint, CancellationToken ct);
}

public sealed class SystemEgressNetwork : IEgressNetwork
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) =>
        await Dns.GetHostAddressesAsync(host, ct);

    public async Task<Stream> DialAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}