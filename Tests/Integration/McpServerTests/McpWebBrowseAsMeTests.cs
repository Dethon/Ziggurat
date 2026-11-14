using Domain.DTOs.Channel;
using Domain.Tools;
using Shouldly;
using static Tests.Integration.McpServerTests.WebIdentityServerHarness;

namespace Tests.Integration.McpServerTests;

// `asMe` is the one way a browse reaches a person's own context, and it is resolved on the server
// from the account the call's conversation context acts for — never from anything the model says,
// nor from the name a channel goes by.
public class McpWebBrowseAsMeTests
{
    private static Dictionary<string, object?> Browse(bool asMe, bool snapshot = false) => new()
    {
        ["url"] = "https://shop.test/orders",
        ["asMe"] = asMe,
        ["snapshot"] = snapshot
    };

    [Fact]
    public async Task AsMeFromAPersonsTurn_BrowsesInTheirAccountsContext_AndTheEnvelopeSaysSo()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: true, snapshot: true), FromTelegram(Fran));

        Envelope(result)["asMe"]!.GetValue<bool>().ShouldBeTrue();
        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBe(Fran.UserId);
        // The composed snapshot is addressed in the same context as the browse it reads.
        server.Browser.Snapshots.ShouldHaveSingleItem().Person.ShouldBe(Fran.UserId);
    }

    // A page's text can talk the model into asMe on any site; the person's session reaches only
    // the sites they handed off, and the refusal points at the handoff instead.
    [Fact]
    public async Task AsMeOnASiteThePersonHasNoLoginFor_IsRefused_AndNothingIsBrowsed()
    {
        await using var server = await StartAsync(loggedIn: false);

        var result = await server.CallAsync("web_browse", Browse(asMe: true), FromTelegram(Fran));

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        Envelope(result)["hint"]!.GetValue<string>().ShouldContain("web_login");
        server.Browser.Browses.ShouldBeEmpty();
    }

    // The account is the person, whichever channel vouched for it: the chat client, Telegram and a
    // recognised voice all reach one context.
    [Fact]
    public async Task EveryChannelOfOneAccount_BrowsesInOneContext()
    {
        await using var server = await StartAsync();

        await server.CallAsync("web_browse", Browse(asMe: true), FromTelegram(Fran));
        await server.CallAsync("web_browse", Browse(asMe: true), FromChatClient(Fran));
        await server.CallAsync("web_browse", Browse(asMe: true), FromVoice(Fran));

        server.Browser.Browses.Select(b => b.Person).ShouldBe([Fran.UserId, Fran.UserId, Fran.UserId]);
    }

    [Fact]
    public async Task TwoAccounts_BrowseInTwoContexts()
    {
        await using var server = await StartAsync();

        await server.CallAsync("web_browse", Browse(asMe: true), FromChatClient(Fran));
        await server.CallAsync("web_browse", Browse(asMe: true), FromChatClient(Ana));

        server.Browser.Browses.Select(b => b.Person).ShouldBe([Fran.UserId, Ana.UserId]);
    }

    // The name a channel goes by is nobody: a Telegram username, or a user id the principal names
    // with no account behind it, reaches no one's context — however much it looks like somebody's.
    [Fact]
    public async Task APrincipalWithNoAccount_IsRefused_AndNothingIsBrowsed()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: true), Context(Fran.UserId, account: null));

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        Envelope(result)["message"]!.GetValue<string>().ShouldContain("nobody vouched");
        server.Browser.Browses.ShouldBeEmpty();
    }

    // The voice channel's guest — nobody the satellite recognised — is a principal with no account.
    [Fact]
    public async Task AsMeFromTheVoiceGuest_IsRefusedSayingTheVoiceWasNotRecognised_AndNothingIsBrowsed()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: true), FromVoiceGuest());

        Envelope(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        Envelope(result)["message"]!.GetValue<string>().ShouldContain("not recognised");
        server.Browser.Browses.ShouldBeEmpty();
    }

    // A fire's userId is whatever its schedule's JSON said and its origin is where it delivers, so
    // neither says who set it going: a fire is somebody only through the principal stamped from the
    // turn that wrote it.
    [Fact]
    public async Task AsMeOnAFireWithNoStampedPrincipal_IsRefused_AndNothingIsBrowsed()
    {
        await using var server = await StartAsync();
        var fire = new ConversationContext("jonas", "conv-1", Fran.UserId, new ReplyTarget("signalr", "conv-1"),
            OriginKind: MessageOriginKind.Schedule);

        var result = await server.CallAsync("web_browse", Browse(asMe: true), fire);

        Envelope(result)["message"]!.GetValue<string>().ShouldContain("nobody vouched");
        server.Browser.Browses.ShouldBeEmpty();
    }

    [Fact]
    public async Task AsMeOnAFireStampedFromAPersonsTurn_BrowsesAsThem()
    {
        await using var server = await StartAsync();
        var fire = new ConversationContext("jonas", "conv-1", "someone-the-json-named", new ReplyTarget("telegram", "conv-1"),
            OriginKind: MessageOriginKind.Schedule, Principal: new Principal("signalr", Fran.UserId, Fran));

        await server.CallAsync("web_browse", Browse(asMe: true), fire);

        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBe(Fran.UserId);
    }

    [Fact]
    public async Task WithoutAsMe_TheBrowseIsAnonymous_EvenForAPersonsTurn()
    {
        await using var server = await StartAsync();

        var result = await server.CallAsync("web_browse", Browse(asMe: false), FromTelegram(Fran));

        Envelope(result)["asMe"].ShouldBeNull();
        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBeNull();
    }

    [Fact]
    public async Task WithoutAsMe_TheVoiceGuestBrowsesAsAnyoneWould()
    {
        await using var server = await StartAsync();

        await server.CallAsync("web_browse", Browse(asMe: false), FromVoiceGuest());

        server.Browser.Browses.ShouldHaveSingleItem().Person.ShouldBeNull();
    }
}