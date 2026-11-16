using System.Net;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The signed-out pages as a browser meets them. Most often that browser is an installed app, which
// has no address bar and no Back button, so every page has to say what to do next on itself. The
// ceremonies these pages begin are PasskeyCeremoniesTests'.
public sealed class AccountsPagesTests
{
    // The installed app opened signed out: sign-in may offer the passkey as soon as it opens (the
    // script does so only in an installed app, where there is nothing else to do on the page).
    [Fact]
    public async Task SignIn_OffersThePasskeyAtOnce()
    {
        await using var server = await AccountsServer.StartAsync();

        var page = await PageAsync(server, "/accounts/sign-in?returnUrl=%2Fcocina");

        page.ShouldContain("data-autostart");
        page.ShouldContain("data-return-url=\"/cocina\"");
    }

    // Having just signed out, the person is told so, and the passkey is not put in front of them again
    // straight away — that would sign them back in with one careless tap.
    [Fact]
    public async Task SignIn_AfterSigningOut_SaysSoAndWaitsToBeAsked()
    {
        await using var server = await AccountsServer.StartAsync();

        var page = await PageAsync(server, "/accounts/sign-in?returnUrl=%2Fcocina&signedOut=1");

        page.ShouldContain("You signed out");
        page.ShouldNotContain("data-autostart");
    }

    // A device with no passkey of the person's is a dead end unless the page says the two ways out:
    // the browser's own "another device" (a phone that holds one), or an owner's recovery link.
    [Fact]
    public async Task SignIn_SaysWhatToDoWithNoPasskeyHere()
    {
        await using var server = await AccountsServer.StartAsync();

        var page = await PageAsync(server, "/accounts/sign-in");

        page.ShouldContain("another device");
        page.ShouldContain("recovery link");
    }

    // The likeliest reason a link no longer works is that it already did: whoever opened it holds a
    // passkey, and signing in is the way on — offered as a button, since nothing else on the page leads anywhere.
    [Fact]
    public async Task ALinkThatCanNoLongerBeUsed_OffersSignIn()
    {
        await using var server = await AccountsServer.StartAsync();

        var response = await server.Client().GetAsync("/accounts/enroll/not-a-link");

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await response.Content.ReadAsStringAsync()).ShouldContain("href=\"/accounts/sign-in\"");
    }

    // The pages follow the theme the person chose in the chat when it is on the same host, before
    // anything is drawn, so they do not flash the other colours on the way in or out.
    [Fact]
    public async Task ThePages_TakeTheChosenThemeBeforeDrawing()
    {
        await using var server = await AccountsServer.StartAsync();

        var page = await PageAsync(server, "/accounts/sign-in");
        var script = await server.Client().GetStringAsync("/accounts/theme.js");

        page.IndexOf("/accounts/theme.js", StringComparison.Ordinal).ShouldBeLessThan(page.IndexOf("<body", StringComparison.Ordinal));
        script.ShouldContain("localStorage");
        page.ShouldContain("name=\"theme-color\"");
        page.ShouldContain("viewport-fit=cover");
    }

    private static async Task<string> PageAsync(AccountsServer server, string path)
    {
        var response = await server.Client().GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }
}