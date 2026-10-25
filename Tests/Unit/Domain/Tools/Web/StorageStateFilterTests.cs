using System.Text.Json.Nodes;
using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class StorageStateFilterTests
{
    private const string State =
        """
        {
          "cookies": [
            { "name": "sid", "value": "1", "domain": ".bank.example", "path": "/" },
            { "name": "sso", "value": "2", "domain": "login.bank.example", "path": "/" },
            { "name": "keep", "value": "3", "domain": "shop.test", "path": "/" }
          ],
          "origins": [
            { "origin": "https://www.bank.example", "localStorage": [{ "name": "t", "value": "x" }] },
            { "origin": "https://shop.test", "localStorage": [{ "name": "cart", "value": "y" }] }
          ]
        }
        """;

    [Fact]
    public void ASiteLeftOut_TakesEveryCookieAndOriginOfItsDomain_AndLeavesTheRest()
    {
        var filtered = JsonNode.Parse(StorageStateFilter.Without(State, ["accounts.bank.example"]))!;

        filtered["cookies"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ShouldBe(["keep"]);
        filtered["origins"]!.AsArray().Select(o => o!["origin"]!.GetValue<string>()).ShouldBe(["https://shop.test"]);
    }

    [Fact]
    public void NoSitesLeftOut_KeepsTheStateWhole()
    {
        var filtered = JsonNode.Parse(StorageStateFilter.Without(State, []))!;

        filtered["cookies"]!.AsArray().Count.ShouldBe(3);
        filtered["origins"]!.AsArray().Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("accounts.google.com", "google.com")]
    [InlineData(".google.com", "google.com")]
    [InlineData("google.com", "google.com")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("online.anz.com.au", "anz.com.au")]
    [InlineData("shop.test", "shop.test")]
    [InlineData("WWW.Example.ORG.", "example.org")]
    [InlineData("192.168.5.45", "192.168.5.45")]
    public void RegistrableDomain_GroupsAHostUnderItsSite(string host, string site)
    {
        RegistrableDomain.Of(host).ShouldBe(site);
    }
}