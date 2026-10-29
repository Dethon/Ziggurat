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
            { "name": "keep", "value": "3", "domain": "shop.test", "path": "/" },
            { "name": "idp", "value": "4", "domain": ".accounts.google.com", "path": "/" }
          ],
          "origins": [
            { "origin": "https://www.bank.example", "localStorage": [{ "name": "t", "value": "x" }] },
            { "origin": "https://shop.test", "localStorage": [{ "name": "cart", "value": "y" }] }
          ]
        }
        """;

    private static IReadOnlyList<string> Cookies(string state) =>
        JsonNode.Parse(state)!["cookies"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();

    private static IReadOnlyList<string> Origins(string state) =>
        JsonNode.Parse(state)!["origins"]!.AsArray().Select(o => o!["origin"]!.GetValue<string>()).ToList();

    [Fact]
    public void Without_ASite_TakesEveryCookieAndOriginOfItsDomain_AndLeavesTheRest()
    {
        var filtered = StorageStateFilter.Without(State, ["accounts.bank.example"]);

        Cookies(filtered).ShouldBe(["keep", "idp"]);
        Origins(filtered).ShouldBe(["https://shop.test"]);
    }

    [Fact]
    public void Without_NoSites_KeepsTheStateWhole()
    {
        var filtered = StorageStateFilter.Without(State, []);

        Cookies(filtered).Count.ShouldBe(4);
        Origins(filtered).Count.ShouldBe(2);
    }

    // A save is an allowlist: what no remembered login names is not written, wherever it came from.
    [Fact]
    public void Only_KeepsTheCookiesAndOriginsOfTheNamedDomains_AndNothingElse()
    {
        var kept = StorageStateFilter.Only(State, ["bank.example", "google.com"]);

        Cookies(kept).ShouldBe(["sid", "sso", "idp"]);
        Origins(kept).ShouldBe(["https://www.bank.example"]);
    }

    [Fact]
    public void Only_NoDomains_KeepsNothing()
    {
        var kept = StorageStateFilter.Only(State, []);

        Cookies(kept).ShouldBeEmpty();
        Origins(kept).ShouldBeEmpty();
    }

    // The save after use exports cookies alone: they replace the stored ones and the stored
    // origins stay as they were.
    [Fact]
    public void WithCookiesOf_ReplacesTheCookies_AndKeepsTheOrigins()
    {
        const string cookiesOnly =
            """{"cookies":[{"name":"sid","value":"rotated","domain":".bank.example","path":"/"}],"origins":[]}""";

        var merged = StorageStateFilter.WithCookiesOf(State, cookiesOnly);

        JsonNode.Parse(merged)!["cookies"]!.AsArray().ShouldHaveSingleItem()!["value"]!.GetValue<string>()
            .ShouldBe("rotated");
        Origins(merged).ShouldBe(["https://www.bank.example", "https://shop.test"]);
    }

    private static string Heavy(int indexedDbBytes, int localStorageBytes) =>
        new JsonObject
        {
            ["cookies"] = new JsonArray(new JsonObject
            {
                ["name"] = "sid", ["value"] = "session", ["domain"] = ".mail.test", ["path"] = "/"
            }),
            ["origins"] = new JsonArray(new JsonObject
            {
                ["origin"] = "https://mail.test",
                ["localStorage"] = new JsonArray(new JsonObject
                {
                    ["name"] = "prefs", ["value"] = new string('l', localStorageBytes)
                }),
                ["indexedDB"] = new JsonArray(new JsonObject
                {
                    ["name"] = "mailbox", ["version"] = 1, ["stores"] = new JsonArray(),
                    ["blob"] = new string('i', indexedDbBytes)
                })
            })
        }.ToJsonString();

    [Fact]
    public void Capped_UnderTheCap_KeepsTheStateWhole()
    {
        var capped = StorageStateFilter.Capped(Heavy(100, 100), 10_000);

        capped.Dropped.ShouldBe(StorageStateFilter.Dropped.Nothing);
        capped.State.ShouldContain("mailbox");
        capped.State.ShouldContain("prefs");
    }

    // IndexedDB is what grows to tens of megabytes; localStorage goes only when that was not
    // enough, and the cookies — the login itself — are always kept.
    [Fact]
    public void Capped_OverTheCap_DropsIndexedDbFirst()
    {
        var capped = StorageStateFilter.Capped(Heavy(20_000, 100), 10_000);

        capped.Dropped.ShouldBe(StorageStateFilter.Dropped.IndexedDb);
        capped.State.ShouldNotContain("mailbox");
        capped.State.ShouldContain("prefs");
        Cookies(capped.State).ShouldBe(["sid"]);
    }

    [Fact]
    public void Capped_StillOverWithoutIndexedDb_DropsLocalStorageToo_AndKeepsTheCookies()
    {
        var capped = StorageStateFilter.Capped(Heavy(20_000, 20_000), 10_000);

        capped.Dropped.ShouldBe(StorageStateFilter.Dropped.IndexedDbAndLocalStorage);
        capped.State.ShouldNotContain("mailbox");
        capped.State.ShouldNotContain("prefs");
        Cookies(capped.State).ShouldBe(["sid"]);
    }
}