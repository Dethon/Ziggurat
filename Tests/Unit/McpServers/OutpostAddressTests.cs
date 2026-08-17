using System.Net;
using McpServerOutpost.Registration;
using Shouldly;

namespace Tests.Unit.McpServers;

// A person starting a binary on their laptop should not have to know which interface the hub can
// reach them on, so the address is worked out from the route toward the hub. That is right on a
// flat network and wrong on a multi-homed one, which is what the override is for.
public class OutpostAddressTests
{
    private const string Hub = "http://192.168.1.10:5000";

    [Fact]
    public void WithNoOverride_TheAddressComesFromTheRouteTowardTheHub()
    {
        Resolve(advertise: null, route: IPAddress.Parse("192.168.1.20"))
            .ShouldBe("http://192.168.1.20:8099/mcp");
    }

    [Fact]
    public void AnOverride_BeatsTheRoute()
    {
        Resolve(advertise: "10.8.0.4", route: IPAddress.Parse("192.168.1.20"))
            .ShouldBe("http://10.8.0.4:8099/mcp");
    }

    // A name is as good as an address here: what the hub needs is something it can dial.
    [Fact]
    public void AnOverrideThatIsAHostName_IsTakenAsGiven()
    {
        Resolve(advertise: "laptop.lan", route: null).ShouldBe("http://laptop.lan:8099/mcp");
    }

    // A bare IPv6 address is not a URI host until it is bracketed, and nothing else adds them.
    [Fact]
    public void AnIpv6Address_IsBracketed()
    {
        Resolve(advertise: "fd00::5", route: null).ShouldBe("http://[fd00::5]:8099/mcp");
    }

    // A registration of something unreachable looks exactly like a machine that is asleep, and
    // nobody would ever find out which it was. So it is a startup failure, with a message that
    // says what to do about it.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.0.0.0")]
    public void NoUsableAddress_FailsSayingSo(string? advertise)
    {
        Should.Throw<InvalidOperationException>(() => Resolve(advertise, route: null))
            .Message.ShouldContain("--advertise");
    }

    // The route is taken toward the hub's host, not toward the whole URL.
    [Fact]
    public void TheRoute_IsAskedForTheHubsHost()
    {
        string? asked = null;
        OutpostAddress.Resolve(Hub, advertise: null, port: 8099, host =>
        {
            asked = host;
            return IPAddress.Loopback;
        });

        asked.ShouldBe("192.168.1.10");
    }

    // A hub on the same machine legitimately routes over loopback, which is what a developer
    // running both wants — so it is an address like any other, not a mistake to refuse.
    [Fact]
    public void ALoopbackRoute_IsAnAddressLikeAnyOther()
    {
        Resolve(advertise: null, route: IPAddress.Loopback).ShouldBe("http://127.0.0.1:8099/mcp");
    }

    private static string Resolve(string? advertise, IPAddress? route) =>
        OutpostAddress.Resolve(Hub, advertise, port: 8099, _ => route);
}