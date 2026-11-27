using Domain.Apps;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// What of the household an app reaches — its reads, the origins it talks to and the servers its asks may
// use — and how an owner's approval narrows it: granted only what is in both, in the manifest's order, and
// what is left over waits on an owner.
public class AppReachTests
{
    private static readonly AppReach _asked = new()
    {
        Reads = ["/sugary", "/notes"],
        Connect = ["https://api.example.com"],
        Asks = ["vault", "sugary"]
    };

    [Fact]
    public void OfAManifest_IsWhatItReadsTalksToAndAsks() =>
        AppReach.Of(new AppManifest
        {
            Name = "Glucose", AccentColor = "#e9601f", Description = "Mine.",
            Reads = ["/sugary"], Connect = ["https://api.example.com"], Asks = ["vault"]
        }).ShouldBe(new AppReach { Reads = ["/sugary"], Connect = ["https://api.example.com"], Asks = ["vault"] });

    [Fact]
    public void WithinAnApproval_IsWhatBothName_InTheManifestsOrder() =>
        _asked.Within(new AppReach { Reads = ["/notes"], Asks = ["homeassistant", "sugary"] })
            .ShouldBe(new AppReach { Reads = ["/notes"], Asks = ["sugary"] });

    [Fact]
    public void BeyondAnApproval_IsWhatWaitsOnAnOwner() =>
        _asked.Beyond(new AppReach { Reads = ["/notes"], Asks = ["sugary"] })
            .ShouldBe(new AppReach { Reads = ["/sugary"], Connect = ["https://api.example.com"], Asks = ["vault"] });

    [Fact]
    public void NoApproval_GrantsNothing()
    {
        _asked.Within(null).IsEmpty.ShouldBeTrue();
        _asked.Beyond(null).ShouldBe(_asked);
    }

    [Fact]
    public void AReachAskingOnlyServers_IsNotEmpty()
    {
        new AppReach { Asks = ["vault"] }.IsEmpty.ShouldBeFalse();
        AppReach.None.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void TwoReaches_AreEqualOnlyWhenTheyAskTheSameServersToo()
    {
        var reads = new AppReach { Reads = ["/sugary"] };

        (reads with { Asks = ["vault"] }).ShouldNotBe(reads);
        (reads with { Asks = ["vault"] }).ShouldBe(new AppReach { Reads = ["/sugary"], Asks = ["vault"] });
        (reads with { Asks = ["vault"] }).GetHashCode().ShouldNotBe(reads.GetHashCode());
    }

    [Fact]
    public void AnApprovedServerOfNoServersShape_IsAProblem() =>
        new AppReach { Asks = ["vault", "mcp-vault.example.com", "Two words"] }.Problems("glucose")
            .ShouldBe([
                "space glucose's approved asks: 'mcp-vault.example.com' is no server's name",
                "space glucose's approved asks: 'Two words' is no server's name"
            ]);
}