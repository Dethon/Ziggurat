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
}