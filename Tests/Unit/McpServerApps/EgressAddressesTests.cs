using System.Net;
using McpServerApps.Checks;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// The check browser's egress reaches the public internet and nothing else: a page's code chooses where it
// goes, and a connect origin's redirect may send it anywhere, so every address that is not global unicast —
// the LAN, the Docker host and its networks, loopback, link-local, CGNAT, documentation and reserved
// ranges — is refused, an IPv4 address written as IPv6 judged as itself.
public class EgressAddressesTests
{
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("151.101.1.229")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    public void APublicAddress_IsReached(string address) =>
        EgressAddresses.IsPublic(IPAddress.Parse(address)).ShouldBeTrue();

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.5.45")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.5.45")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::c0a8:52d")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:c0a8:052d::1")]
    [InlineData("2001::1")]
    public void AnyOtherAddress_IsRefused(string address) =>
        EgressAddresses.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();
}