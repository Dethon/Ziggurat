using Domain.Tools;
using Shouldly;
using static Tests.Integration.McpServerTests.WebIdentityServerHarness;

namespace Tests.Integration.McpServerTests;

// `asMe` is the one way a browse reaches a person's own context, and it is resolved on the server
// from the call's conversation context through the identity map — never from anything the model
// says.
public class McpWebBrowseAsMeTests
{
    private static Dictionary<string, object?> Browse(bool asMe, bool snapshot = false) => new()
    {
        ["url"] = "https://shop.test/orders",
        ["asMe"] = asMe,
        ["snapshot"] = snapshot
    };

    [Fact]
    public async Task AsMeFromAMappedUser_BrowsesInThatPersonsContext_AndTheEnvelopeSaysSo()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: true, snapshot: true), Context("fran_tg"));

        Envelope(result)["asMe"]!.GetValue<bool>().ShouldBeTrue();
        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBe("fran");
        // The composed snapshot is addressed in the same context as the browse it reads.
        server.Browser.Snapshots.ShouldHaveSingleItem().Person.ShouldBe("fran");
    }

    // A page's text can talk the model into asMe on any site; the person's session reaches only
    // the sites they handed off, and the refusal points at the handoff instead.
    [Fact]
    public async Task AsMeOnASiteThePersonHasNoLoginFor_IsRefused_AndNothingIsBrowsed()
    {
        await using var server = await StartAsync(loggedIn: false);

        var result = await server.CallAsync("web_browse", Browse(asMe: true), Context("fran_tg"));

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        Envelope(result)["hint"]!.GetValue<string>().ShouldContain("web_login");
        server.Browser.Browses.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoIdsOfOnePerson_BrowseInOneContext()
    {
        await using var server = await StartAsync();

        await server.CallAsync("web_browse", Browse(asMe: true), Context("fran_tg"));
        await server.CallAsync("web_browse", Browse(asMe: true), Context("fran", channelId: "voice"));

        server.Browser.Browses.Select(b => b.Person).ShouldBe(["fran", "fran"]);
    }

    // WebChat's user is whatever its page's picker sent, so its turns are nobody until it is
    // verified — even naming a real person's WebChat name.
    [Fact]
    public async Task AsMeFromWebChat_WhileUnverified_IsRefused_AndNothingIsBrowsed()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: true), Context("Fran", channelId: "signalr"));

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        server.Browser.Browses.ShouldBeEmpty();
    }

    // An id is matched only on the channel that issued it: fran's WebChat name sent from Telegram
    // is a Telegram username nobody holds.
    [Fact]
    public async Task AnotherChannelsIdOfAPerson_IsNotThem()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: true), Context("Fran", channelId: "telegram"));

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        server.Browser.Browses.ShouldBeEmpty();
    }

    [Fact]
    public async Task AsMeFromWebChat_OnceVerified_BrowsesAsTheNamedPerson()
    {
        await using var server = await StartAsync(
            settings: s => s with { Identity = s.Identity with { VerifiedChannels = WebChatVerified } });

        await server.CallAsync("web_browse", Browse(asMe: true), Context("Fran", channelId: "signalr"));

        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBe("fran");
    }

    [Fact]
    public async Task WithoutAsMe_TheBrowseIsAnonymous_EvenForAMappedUser()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: false), Context("fran_tg"));

        Envelope(result)["asMe"].ShouldBeNull();
        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBeNull();
    }

    [Theory]
    [InlineData("stranger", ToolError.Codes.PermissionDenied)]
    [InlineData("household", ToolError.Codes.PermissionDenied)]
    public async Task AsMeFromNoPerson_IsRefused_AndNothingIsBrowsed(string userId, string code)
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: true), Context(userId, channelId: "voice"));

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(code);
        server.Browser.Browses.ShouldBeEmpty();
    }

    [Fact]
    public async Task AsMeWithNoIdentityMap_IsRefusedAsTheFeatureOff()
    {
        await using var server = await StartAsync(people: []);

        var result = await server.CallAsync("web_browse", Browse(asMe: true), Context("fran_tg"));

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        server.Browser.Browses.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoIdentityMap_LeavesOrdinaryBrowsingAlone()
    {
        await using var server = await StartAsync(people: []);

        await server.CallAsync("web_browse", Browse(asMe: false), Context("fran_tg"));

        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBeNull();
    }
}