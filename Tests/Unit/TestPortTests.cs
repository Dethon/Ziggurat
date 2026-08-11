using System.Collections.Concurrent;
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
}