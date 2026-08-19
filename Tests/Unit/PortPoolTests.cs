using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Unit;

// The pool behind TestPort, tested against a fake probe: exhaustion and reuse are about the
// bookkeeping, and a test that needed a thousand real kernel binds to see the band run out
// would be reproducing the failure it exists to prevent.
public class PortPoolTests
{
    [Fact]
    public void Get_BandWalkedToItsEnd_Throws()
    {
        var pool = new PortPool(100, 103, _ => true);

        var issued = Enumerable.Range(0, 3).Select(_ => pool.Get()).ToList();

        issued.ShouldBe([100, 101, 102]);
        Should.Throw<InvalidOperationException>(() => pool.Get());
    }

    // The full eval tier starts seven servers per run and retries each scenario, which walked the
    // band to its end mid-suite while every one of those servers was long stopped. A port given
    // back is the only thing that lets a bounded band outlive an unbounded number of runs.
    [Fact]
    public void Get_AfterARelease_HandsTheReleasedPortOutAgain()
    {
        var pool = new PortPool(100, 103, _ => true);
        var first = pool.Get();
        pool.Get();
        pool.Get();

        pool.Release(first);

        pool.Get().ShouldBe(first);
    }

    [Fact]
    public void Get_ReleasedPortNoLongerFree_WalksOnInsteadOfHandingItOut()
    {
        var busy = new HashSet<int>();
        var pool = new PortPool(100, 110, port => !busy.Contains(port));
        var first = pool.Get();

        pool.Release(first);
        busy.Add(first);

        pool.Get().ShouldBe(first + 1);
    }

    [Fact]
    public void Release_OfTheSamePortTwice_DoesNotHandItToTwoCallers()
    {
        var pool = new PortPool(100, 110, _ => true);
        var first = pool.Get();

        pool.Release(first);
        pool.Release(first);

        pool.Get().ShouldBe(first);
        pool.Get().ShouldNotBe(first);
    }

    [Fact]
    public void Release_OfAPortNeverIssued_IsIgnored()
    {
        var pool = new PortPool(100, 110, _ => true);

        pool.Release(105);

        pool.Get().ShouldBe(100);
    }
}