using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

// The site a host belongs to, by the public suffix list: what a login is a login to, and what its
// cookies and storage are grouped under when a save decides what to keep.
public class RegistrableDomainTests
{
    [Theory]
    [InlineData("accounts.google.com", "google.com")]
    [InlineData(".google.com", "google.com")]
    [InlineData("google.com", "google.com")]
    [InlineData("WWW.Example.ORG.", "example.org")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("online.anz.com.au", "anz.com.au")]
    [InlineData("a.b.c.example.co.jp", "example.co.jp")]
    public void Of_AHostUnderAnIcannSuffix_IsItsSuffixAndOneLabelMore(string host, string site)
    {
        RegistrableDomain.Of(host).ShouldBe(site);
    }

    // A shared host's users are separate sites: two people's pages on github.io are not one login.
    [Theory]
    [InlineData("alice.github.io", "alice.github.io")]
    [InlineData("docs.alice.github.io", "alice.github.io")]
    [InlineData("bob.github.io", "bob.github.io")]
    [InlineData("myapp.herokuapp.com", "myapp.herokuapp.com")]
    public void Of_AHostUnderAPrivateSuffix_KeepsItsUsersApart(string host, string site)
    {
        RegistrableDomain.Of(host).ShouldBe(site);
    }

    // *.ck makes every second-level label a suffix of its own; !www.ck says www.ck is not one.
    [Theory]
    [InlineData("shop.anything.ck", "shop.anything.ck")]
    [InlineData("a.shop.anything.ck", "shop.anything.ck")]
    [InlineData("www.ck", "www.ck")]
    [InlineData("sub.www.ck", "www.ck")]
    public void Of_WildcardAndExceptionRules_AreFollowed(string host, string site)
    {
        RegistrableDomain.Of(host).ShouldBe(site);
    }

    // The fixtures' own hosts sit under no listed suffix, so the default rule — the last label —
    // applies; a single label, an address and a suffix on its own are what they are.
    [Theory]
    [InlineData("shop.test", "shop.test")]
    [InlineData("accounts.shop.test", "shop.test")]
    [InlineData("remembered-0123.test", "remembered-0123.test")]
    [InlineData("bank.example", "bank.example")]
    [InlineData("localhost", "localhost")]
    [InlineData("192.168.5.45", "192.168.5.45")]
    [InlineData("[::1]", "[::1]")]
    [InlineData("github.io", "github.io")]
    [InlineData("co.uk", "co.uk")]
    public void Of_HostsWithNoRegistrableDomain_AreKeptAsTheyAre(string host, string site)
    {
        RegistrableDomain.Of(host).ShouldBe(site);
    }

    [Fact]
    public void Of_AnInternationalisedHost_MatchesItsSuffixInEitherSpelling()
    {
        RegistrableDomain.Of("www.example.xn--p1ai").ShouldBe("example.xn--p1ai");
        RegistrableDomain.Of("www.пример.рф").ShouldBe("пример.рф");
    }
}