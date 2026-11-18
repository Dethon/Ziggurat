using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Unit;

// A test about the test suite's own port allocator, because a duplicate here does not fail here:
// it fails in whichever unrelated server happened to be handed the same number.
public class TestPortTests
{
    [Fact]
    public void GetAvailable_AskedFromEveryThreadAtOnce_NeverHandsOutTheSamePortTwice()
    {
        // The port is chosen by binding zero and released again so the caller can bind it, which
        // leaves the number available to the next asker until they do. Serially nobody noticed;
        // with collections running in parallel two servers were handed one port and the second
        // died on startup with "address already in use", failing a test that had nothing to do
        // with either.
        var ports = new ConcurrentBag<int>();

        Parallel.For(0, 200, _ => ports.Add(TestPort.GetAvailable()));

        ports.Distinct().Count().ShouldBe(ports.Count);
    }

    // Not handing the same number out twice is only half of it. The probe used to bind zero, which
    // asks the kernel for an *ephemeral* port — the very range it draws from when a container
    // publishes a random host port or anything here opens an outbound socket. The number was ours
    // for as long as it took the caller to bind it, and a run with enough going on at once lost
    // that race: a library server died on "address already in use" at 45201, inside this machine's
    // 44620-48715 ephemeral range, failing a test that had nothing to do with ports.
    //
    // A port below that range is one the kernel will never assign on its own, so the only thing
    // that can take it is a caller who asked for it by number — and this is the only thing here
    // that does.
    [Fact]
    public void GetAvailable_NeverReturnsAPortTheKernelHandsOutOnItsOwn()
    {
        var ephemeralLow = TestPort.EphemeralRangeStart;

        var ports = Enumerable.Range(0, 50).Select(_ => TestPort.GetAvailable()).ToList();

        ports.ShouldAllBe(port => port < ephemeralLow);
    }

    // Docker allocates a container's random host port from its own daemon's range, which is not this
    // machine's: under Docker Desktop the daemon runs in a VM on the kernel default 32768-60999, which
    // swallows WSL's 44620-48715 and the band beneath it. In pass 20's full suite a random-port
    // container took 41980 between the dashboard fixture's probe and its Caddy's start, and five
    // dashboard E2Es died on "failed to bind host port 0.0.0.0:41980: address already in use". The
    // band sits below Docker's default range as well.
    [Fact]
    public void GetAvailable_NeverReturnsAPortDockerHandsOutForARandomBinding()
    {
        var ports = Enumerable.Range(0, 50).Select(_ => TestPort.GetAvailable()).ToList();

        ports.ShouldAllBe(port => port < TestPort.DockerEphemeralRangeStart);
    }

    // The claim and the probe keep test processes apart, and nothing keeps out the rest of the machine:
    // under WSL's mirrored networking Windows shares this port space, and a server beside the suite
    // once failed on "address already in use" at a port the probe had just found free. A server that
    // loses its port that way is started on another rather than failing a test about something else.
    [Fact]
    public async Task StartWebAsync_APortTakenBetweenTheProbeAndTheBind_StartsOnAnother()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;
        var offered = new Queue<int>([taken, TestPort.GetAvailable()]);

        var (port, app) = await TestPort.StartWebAsync(port =>
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
            var built = builder.Build();
            built.MapGet("/", () => "here");
            return built;
        }, offered.Dequeue);
        await using var _ = app;

        port.ShouldNotBe(taken);
        using var http = new HttpClient();
        (await http.GetStringAsync($"http://127.0.0.1:{port}/")).ShouldBe("here");
    }
}