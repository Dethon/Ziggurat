using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using McpServerApps.Checks;
using McpServerApps.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The check browser's one way out, as the browser speaks to it: CONNECT host:port for every https origin a
// page reaches, a new tunnel for every hop a redirect sends it on. Each name is resolved once and every
// address it answers with judged; a tunnel is dialled to a judged address, never the name again. The network
// below the judgment is arranged here (names answering what a test says, dials recorded and sent to a local
// echo), since the public addresses the egress lets through are nowhere on a test machine.
public sealed class CheckEgressTests : IAsyncLifetime
{
    private static readonly IPAddress _public = IPAddress.Parse("93.184.215.14");
    private readonly ArrangedNetwork _network = new();
    private CheckEgress _egress = null!;
    private IPEndPoint _at = null!;

    // A port found free can still be taken by the rest of the machine before the bind (WSL's mirrored
    // networking shares it with Windows), so a lost bind starts again on another.
    public async Task InitializeAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = TestPort.GetAvailable();
            var settings = new AppsSettings { AppsDomain = "apps.test", Listeners = new AppListeners { Egress = port } };
            _egress = new CheckEgress(settings, _network, NullLogger<CheckEgress>.Instance);
            await _egress.StartAsync(CancellationToken.None);
            try
            {
                _at = await _egress.Listening.WaitAsync(TimeSpan.FromSeconds(30));
                return;
            }
            catch (SocketException e) when (attempt < 5 && e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                await _egress.StopAsync(CancellationToken.None);
                _egress.Dispose();
            }
        }
    }

    public async Task DisposeAsync()
    {
        await _egress.StopAsync(CancellationToken.None);
        _egress.Dispose();
        TestPort.Release(_at.Port);
        await _network.DisposeAsync();
    }

    // A port it cannot bind is said at once to whoever waits on the listener, not after their own deadline.
    [Fact]
    public async Task APortItCannotBind_FailsTheListenerAtOnce()
    {
        var taken = TestPort.GetAvailable();
        using var holder = new TcpListener(IPAddress.Any, taken);
        holder.Start();
        var settings = new AppsSettings { AppsDomain = "apps.test", Listeners = new AppListeners { Egress = taken } };
        using var egress = new CheckEgress(settings, _network, NullLogger<CheckEgress>.Instance);

        await egress.StartAsync(CancellationToken.None);

        (await Should.ThrowAsync<SocketException>(egress.Listening.WaitAsync(TimeSpan.FromSeconds(10))))
            .SocketErrorCode.ShouldBe(SocketError.AddressAlreadyInUse);
        holder.Stop();
        TestPort.Release(taken);
    }

    // A public origin is tunnelled to, at the address it was judged on, and the tunnel carries both ways.
    [Fact]
    public async Task APublicOrigin_IsTunnelledTo_AtTheAddressItWasJudgedOn()
    {
        _network.Answer("cdn.example.org", _public);

        using var browser = await ConnectAsync("cdn.example.org:443");
        var answer = await ReadHeadAsync(browser);
        await browser.GetStream().WriteAsync("hello"u8.ToArray());
        var echoed = new byte[5];
        await browser.GetStream().ReadExactlyAsync(echoed);

        answer.ShouldStartWith("HTTP/1.1 200");
        Encoding.ASCII.GetString(echoed).ShouldBe("hello");
        _network.Dialled.ShouldBe([new IPEndPoint(_public, 443)]);
    }

    // A connect origin the author controls answers 307 to a LAN address; the browser follows it with a new
    // tunnel there, by name or by address, and every such hop is refused before anything is dialled. A name
    // answering one public and one private address is refused whole.
    [Theory]
    [InlineData("192.168.5.45:443", null)]
    [InlineData("127.0.0.1:8080", null)]
    [InlineData("[::1]:443", null)]
    [InlineData("[::ffff:10.0.0.1]:443", null)]
    [InlineData("169.254.169.254:80", null)]
    [InlineData("lan.example.org:443", "192.168.5.45")]
    [InlineData("host.docker.internal:443", "172.17.0.1")]
    [InlineData("both.example.org:443", "10.0.0.7")]
    public async Task ARedirectsNextHopToAPrivateAddress_IsRefused_AndNothingIsDialled(string target, string? resolvesTo)
    {
        if (resolvesTo is not null)
        {
            _network.Answer(target.Split(':')[0], target.StartsWith("both", StringComparison.Ordinal)
                ? [_public, IPAddress.Parse(resolvesTo)]
                : [IPAddress.Parse(resolvesTo)]);
        }

        using var browser = await ConnectAsync(target);

        (await ReadHeadAsync(browser)).ShouldStartWith("HTTP/1.1 403");
        _network.Dialled.ShouldBeEmpty();
    }

    // DNS rebinding: a name judged public and answering a private address when asked again is never asked
    // again; the tunnel goes to the address it was judged on.
    [Fact]
    public async Task ANameThatAnswersPrivateTheSecondTime_IsNeverAskedASecondTime()
    {
        _network.Answer("rebind.example.org", _public);
        _network.Answer("rebind.example.org", IPAddress.Parse("192.168.5.45"));

        using var browser = await ConnectAsync("rebind.example.org:443");

        (await ReadHeadAsync(browser)).ShouldStartWith("HTTP/1.1 200");
        _network.Dialled.ShouldBe([new IPEndPoint(_public, 443)]);
        _network.Asked("rebind.example.org").ShouldBe(1);
    }

    // Everything a check page reaches through it is https: a request to forward is no tunnel, and refused.
    [Fact]
    public async Task APlainHttpRequest_IsRefused()
    {
        using var browser = new TcpClient();
        await browser.ConnectAsync(_at);
        await browser.GetStream().WriteAsync("GET http://192.168.5.45/ HTTP/1.1\r\nHost: 192.168.5.45\r\n\r\n"u8.ToArray());

        (await ReadHeadAsync(browser)).ShouldStartWith("HTTP/1.1 405");
        _network.Dialled.ShouldBeEmpty();
    }

    private async Task<TcpClient> ConnectAsync(string target)
    {
        var browser = new TcpClient();
        await browser.ConnectAsync(_at);
        await browser.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"));
        return browser;
    }

    private static async Task<string> ReadHeadAsync(TcpClient client)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await client.GetStream().ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(30)) == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }

    // Names answer what the test says, in order (the last answer repeats); every dial is recorded and reaches
    // a local echo.
    private sealed class ArrangedNetwork : IEgressNetwork, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<IPAddress[]>> _answers = new();
        private readonly ConcurrentDictionary<string, int> _asked = new();
        private readonly ConcurrentQueue<IPEndPoint> _dialled = new();
        private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public ArrangedNetwork()
        {
            _echo.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _echo.AcceptTcpClientAsync(_stop.Token);
                    _ = client.GetStream().CopyToAsync(client.GetStream(), _stop.Token).ContinueWith(_ => client.Dispose(), TaskScheduler.Default);
                }
            });
        }

        public IReadOnlyList<IPEndPoint> Dialled => [.. _dialled];

        public int Asked(string host) => _asked.GetValueOrDefault(host);

        public void Answer(string host, params IPAddress[] addresses) =>
            _answers.GetOrAdd(host, _ => new ConcurrentQueue<IPAddress[]>()).Enqueue(addresses);

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
        {
            _asked.AddOrUpdate(host, 1, (_, n) => n + 1);
            var queue = _answers.GetValueOrDefault(host);
            IReadOnlyList<IPAddress> answer = queue is null ? []
                : queue.Count > 1 && queue.TryDequeue(out var next) ? next
                : queue.TryPeek(out var last) ? last : [];
            return Task.FromResult(answer);
        }

        public async Task<Stream> DialAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            _dialled.Enqueue(endpoint);
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)_echo.LocalEndpoint, ct);
            return client.GetStream();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _echo.Stop();
            _stop.Dispose();
        }
    }
}