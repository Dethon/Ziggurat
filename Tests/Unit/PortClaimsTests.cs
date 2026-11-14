using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Unit;

// Two test processes side by side walk the same band: a port one of them claimed is never the
// other's, until the first lets it go. Two claim sets over one directory stand in for the two
// processes — the lock is on an open file, so they exclude each other as two processes do.
public class PortClaimsTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "port-claims-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void APortAnotherRunClaimed_IsNotClaimedAgain_UntilItIsReleased()
    {
        var mine = new PortClaims(_directory);
        var theirs = new PortClaims(_directory);

        theirs.TryClaim(7001).ShouldBeTrue();
        mine.TryClaim(7001).ShouldBeFalse();
        mine.TryClaim(7002).ShouldBeTrue();

        theirs.Release(7001);
        mine.TryClaim(7001).ShouldBeTrue();
    }

    [Fact]
    public void APortThisRunHolds_IsNotClaimedTwice()
    {
        var mine = new PortClaims(_directory);

        mine.TryClaim(7003).ShouldBeTrue();
        mine.TryClaim(7003).ShouldBeFalse();
    }
}