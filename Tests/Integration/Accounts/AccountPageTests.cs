using System.Net;
using Accounts.Sessions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A person's own page, as their browser meets it: what it lists, and what its forms change. The
// ceremonies it begins (adding a passkey, revoking one that began sessions) are in
// PasskeyCeremoniesTests, which can make a passkey.
public class AccountPageTests
{
    private const string Account = "/accounts/account";

    [Fact]
    public async Task SignedOut_ThePageSendsToSignInAndBack()
    {
        await using var server = await AccountsServer.StartAsync();

        var response = await server.Client().GetAsync(Account);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldBe(new Uri(server.Address,
            "/accounts/sign-in?returnUrl=" + Uri.EscapeDataString(Account)));
    }

    [Fact]
    public async Task ThePage_ListsTheDevicesSignedIn()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var sessions = server.Service<SessionStore>();
        var phone = (await sessions.IssueAsync(fran.UserId, null, "Pixel phone browser", CancellationToken.None))!;
        await sessions.IssueAsync(fran.UserId, null, "Laptop browser", CancellationToken.None);

        var page = await server.SignedIn(phone.Token).PageAsync(Account);

        page.ShouldContain("Fran");
        page.ShouldContain("Pixel phone browser");
        page.ShouldContain("Laptop browser");
    }

    [Fact]
    public async Task RevokingADevice_EndsThatSessionAlone()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var sessions = server.Service<SessionStore>();
        var phone = (await sessions.IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var laptop = (await sessions.IssueAsync(fran.UserId, null, "laptop", CancellationToken.None))!;

        var response = await server.SignedIn(phone.Token)
            .PostAsync(Account, $"{Account}/sessions/{laptop.Id}/revoke");

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        (await server.VerifyAsync(laptop.Token, accept: "application/json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.VerifyAsync(phone.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await server.SignedIn(phone.Token).PageAsync(Account)).ShouldNotContain("laptop");
    }

    // A session id is not a secret, so naming somebody else's is not enough to end it.
    [Fact]
    public async Task RevokingAnotherPersonsDevice_EndsNothing()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var sessions = server.Service<SessionStore>();
        var frans = (await sessions.IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var lauras = await server.MintSessionAsync(laura);

        var response = await server.SignedIn(lauras).PostAsync(Account, $"{Account}/sessions/{frans.Id}/revoke");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await server.VerifyAsync(frans.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The pages' forms change who gets in, so a post that did not come from one of them is refused
    // even from a signed-in browser.
    [Fact]
    public async Task APostWithoutTheFormsToken_IsRefused()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var sessions = server.Service<SessionStore>();
        var phone = (await sessions.IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var laptop = (await sessions.IssueAsync(fran.UserId, null, "laptop", CancellationToken.None))!;

        var response = await server.SignedIn(phone.Token).PostWithoutPageAsync($"{Account}/sessions/{laptop.Id}/revoke");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await server.VerifyAsync(laptop.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ThePage_ShowsTheVoiceEnrollmentCommandForThePersonsId()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");

        var page = await server.SignedIn(await server.MintSessionAsync(laura)).PageAsync(Account);

        page.ShouldContain($"scripts/enroll-voice.sh {laura}");
    }

    [Fact]
    public async Task ATelegramCode_IsADeepLinkToTheDeploymentsBot()
    {
        await using var server = await AccountsServer.StartAsync(s => s with { Telegram = new() { BotUsername = "ziggurat_bot" } });
        var laura = await server.SeedMemberAsync("Laura");
        var browser = server.SignedIn(await server.MintSessionAsync(laura));

        var response = await browser.PostAsync(Account, $"{Account}/telegram");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadAsStringAsync();
        var link = System.Text.RegularExpressions.Regex.Match(page, @"https://t\.me/ziggurat_bot\?start=(?<code>[A-Za-z0-9_-]+)");
        link.Success.ShouldBeTrue(page);
        // Telegram passes on a start parameter of at most 64 of these characters.
        link.Groups["code"].Value.Length.ShouldBeLessThanOrEqualTo(64);
    }

    [Fact]
    public async Task WithNoBotConfigured_TheTelegramCodeIsShownBare()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var browser = server.SignedIn(await server.MintSessionAsync(laura));

        var page = await (await browser.PostAsync(Account, $"{Account}/telegram")).Content.ReadAsStringAsync();

        page.ShouldNotContain("t.me/");
        page.ShouldMatch(@"<code data-telegram-code>[A-Za-z0-9_-]{20,64}</code>");
    }

    // Everyone in the household can see who reads every conversation, members included.
    [Fact]
    public async Task ThePage_NamesWhoSeesAllConversations()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var pablo = await server.SeedMemberAsync("Pablo");
        var owner = server.SignedIn(await server.MintSessionAsync(fran.UserId));
        (await owner.PostAsync("/accounts/members", $"/accounts/members/{laura}/sees-all", ("grant", "true")))
            .StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        var page = await server.SignedIn(await server.MintSessionAsync(pablo)).PageAsync(Account);

        var start = page.IndexOf("data-sees-all", StringComparison.Ordinal);
        var holders = page[start..page.IndexOf("</section>", start, StringComparison.Ordinal)];
        holders.ShouldContain("Laura");
        holders.ShouldNotContain("Fran");
        holders.ShouldNotContain("Pablo");
    }
}