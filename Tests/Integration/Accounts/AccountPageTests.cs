using System.Net;
using Accounts.Configuration;
using Accounts.Sessions;
using Accounts.Store;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Microsoft.EntityFrameworkCore;
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

    // When a device signed in is said on the household's clock and day first, as the admin pages and
    // the dashboard say a time — not in UTC, which put a person in Madrid an hour behind their own day.
    [Fact]
    public async Task ThePage_SaysWhenOnTheHouseholdsClock_DayFirst()
    {
        await using var server = await AccountsServer.StartAsync();
        server.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 3, 0, 8, 0, TimeSpan.Zero));
        var fran = await server.MintOperatorAsync("Fran");
        await server.Service<HouseholdConfigStore>().SaveAsync(fran.TenantId, HouseholdSections.SettingsSchema,
            HouseholdSections.SettingsSchema.Write(new HouseholdSettings { Name = "Casa", TimeZone = "America/New_York" }),
            CancellationToken.None);
        var session = await server.MintSessionAsync(fran.UserId);

        var page = await server.SignedIn(session).PageAsync(Account);

        page.ShouldContain("Signed in 02/11/2026 19:08");
        // The device's own line, not the whole page: the page carries a random antiforgery token, and
        // "utc" read case-insensitively turns up in one now and then.
        System.Text.RegularExpressions.Regex.Match(page, "Signed in [^<]*").Value.ShouldNotContain("UTC");
    }

    // An app opens this page by navigating away from itself, and an installed app has no Back button:
    // with no way back on the page, a person who looked at their account was stuck there until they
    // closed the app. Back is where the app said it came from, when that is one of the deployment's
    // own; otherwise the front of whichever host the page is on — the chat's, Sugary's, the dashboard's.
    [Theory]
    [InlineData("/cocina?topic=7", "/cocina?topic=7")]
    [InlineData("https://elsewhere.example/phish", "/")]
    [InlineData(null, "/")]
    public async Task ThePage_LeadsBackWhereItWasOpenedFrom(string? returnUrl, string back)
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var session = await server.MintSessionAsync(fran.UserId);

        var page = await server.SignedIn(session).PageAsync(WithReturn(returnUrl));

        System.Text.RegularExpressions.Regex.Match(page, "<a [^>]*data-back[^>]*>").Value
            .ShouldContain($"href=\"{System.Net.WebUtility.HtmlEncode(back)}\"");
    }

    // A form posts and comes back to the page, and the way back must survive that trip: saving a name
    // used to drop where the app had sent the person from, and Back then went to the chat's front page.
    [Fact]
    public async Task AFormsRoundTrip_KeepsWhereToGoBack()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var browser = server.SignedIn(await server.MintSessionAsync(laura));
        var here = WithReturn("/cocina");

        var page = await browser.PageAsync(here);
        page.ShouldContain($"action=\"{Account}/profile?returnUrl=%2Fcocina\"");

        var response = await browser.PostAsync(here, $"{Account}/profile?returnUrl=%2Fcocina", ("displayName", "Laura"), ("avatarUrl", ""));

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        response.Headers.Location!.ToString().ShouldBe(here);
    }

    // Signing out is what a person comes to this page for most, so it is one plain button rather
    // than a row to find among the devices, and it leaves for sign-in — saying they signed out, and
    // not offering the passkey at once — which then brings them back to the app, not to this page.
    [Fact]
    public async Task SigningOut_EndsThisDevicesSessionAndLeavesForSignInAndBack()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var sessions = server.Service<SessionStore>();
        var phone = (await sessions.IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var laptop = (await sessions.IssueAsync(fran.UserId, null, "laptop", CancellationToken.None))!;
        var browser = server.SignedIn(phone.Token);

        var page = await browser.PageAsync(WithReturn("/cocina"));
        page.ShouldContain($"action=\"/accounts/sign-out?returnUrl=%2Fcocina\"");
        var response = await browser.PostAsync(WithReturn("/cocina"), "/accounts/sign-out?returnUrl=%2Fcocina");

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        response.Headers.Location.ShouldBe(new Uri(server.Address, "/accounts/sign-in?returnUrl=%2Fcocina&signedOut=1"));
        response.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("ziggurat_session=;", StringComparison.Ordinal));
        (await server.VerifyAsync(phone.Token, accept: "application/json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.VerifyAsync(laptop.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SigningOutWithoutTheFormsToken_IsRefused()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var session = await server.MintSessionAsync(fran.UserId);

        var response = await server.SignedIn(session).PostWithoutPageAsync("/accounts/sign-out");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await server.VerifyAsync(session)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Signing out this device from its own row is the same sign-out: it used to come back to this
    // page, which then sent the now signed-out person to sign in and back to the account page.
    [Fact]
    public async Task SigningOutThisDeviceFromItsRow_LeavesForSignInAndBack()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var phone = (await server.Service<SessionStore>().IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var browser = server.SignedIn(phone.Token);

        var response = await browser.PostAsync(WithReturn("/cocina"), $"{Account}/sessions/{phone.Id}/revoke?returnUrl=%2Fcocina");

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        response.Headers.Location.ShouldBe(new Uri(server.Address, "/accounts/sign-in?returnUrl=%2Fcocina&signedOut=1"));
    }

    // A device is listed as a person would name it, its user agent kept for whoever wants it.
    [Fact]
    public async Task ThePage_NamesEachDeviceByItsBrowserAndSystem()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        const string android =
            "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";
        var phone = (await server.Service<SessionStore>().IssueAsync(fran.UserId, null, android, CancellationToken.None))!;

        var page = await server.SignedIn(phone.Token).PageAsync(Account);

        page.ShouldContain(">Chrome on Android<");
        page.ShouldContain($"title=\"{System.Net.WebUtility.HtmlEncode(android)}\"");
    }

    // A stale form's page is the one a person meets with no Back button to "go back" with: it links
    // to the page again, fresh, keeping the way back.
    [Fact]
    public async Task AStaleForm_LinksToThePageAgain()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var session = await server.MintSessionAsync(fran.UserId);

        var response = await server.SignedIn(session).PostWithoutPageAsync($"{Account}/telegram?returnUrl=%2Fcocina");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain($"href=\"{Account}?returnUrl=%2Fcocina\"");
    }

    private static string WithReturn(string? returnUrl) =>
        returnUrl is null ? Account : $"{Account}?returnUrl={Uri.EscapeDataString(returnUrl)}";

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

    // The chat client sends a page refused as suspended here, with where it was. Nothing pushes to a
    // page the hub let go, so the notice itself asks again until the household is resumed and then
    // goes back there: where it says, if that is one of the deployment's, else this page again.
    [Theory]
    [InlineData("/cocina", "/cocina")]
    [InlineData("https://elsewhere.example/phish", null)]
    [InlineData(null, null)]
    public async Task ASuspendedHouseholdsNotice_AsksAgainAndSaysWhereToGoBack(string? returnUrl, string? goesBackTo)
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var browser = server.SignedIn(await server.MintSessionAsync(ana));
        (await server.Admin(await server.MintSessionAsync(fran.UserId))
            .PutAsync(AdminProtocol.Suspension(casa), new HouseholdSuspension(true))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await browser.GetAsync(returnUrl is null ? Account : $"{Account}?returnUrl={Uri.EscapeDataString(returnUrl)}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var page = await response.Content.ReadAsStringAsync();
        page.ShouldContain("data-suspended");
        if (goesBackTo is null)
        {
            page.ShouldNotContain("data-return");
        }
        else
        {
            page.ShouldContain($"data-return=\"{goesBackTo}\"");
        }
    }

    // Nothing here is open to a suspended household's people, as nothing behind the session check is:
    // no Telegram code, no new passkey, no profile change, each told why — the page in the check's own
    // words, the passkey script's call as plain text it can show.
    [Fact]
    public async Task ASuspendedHouseholdsPerson_IsRefusedThePageItsFormsAndItsScript_WithTheNotice()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var browser = server.SignedIn(await server.MintSessionAsync(ana));
        (await server.Admin(await server.MintSessionAsync(fran.UserId))
            .PutAsync(AdminProtocol.Suspension(casa), new HouseholdSuspension(true))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = new[]
        {
            await browser.GetAsync(Account),
            await browser.PostWithoutPageAsync($"{Account}/telegram"),
            await browser.PostWithoutPageAsync($"{Account}/profile", ("displayName", "Someone else"), ("avatarUrl", "")),
            await browser.PostWithoutPageAsync("/accounts/api/passkeys/options")
        };

        foreach (var response in refused)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync()).ShouldContain(SessionCheck.SuspendedNotice);
        }

        refused[^1].Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.Users.SingleAsync(u => u.Id == ana)).DisplayName.ShouldBe("Ana");
        (await db.Links.CountAsync(l => l.TenantId == casa)).ShouldBe(0);
    }

    [Fact]
    public async Task ThePage_ShowsTheVoiceEnrollmentCommandForThePersonsId()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");

        var page = await server.SignedIn(await server.MintSessionAsync(laura)).PageAsync(Account);

        page.ShouldContain($"scripts/enroll-voice.sh {laura}");
        // Which agent answers aloud is each household's own choice (its voice agent), so the page names
        // the satellites a person enrolls with, never the home's Nabu.
        page.ShouldContain("So that the household's voice satellites know your voice");
        page.ShouldNotContain("nabu");
    }

    // A household with no voice server of its own has no satellites to enroll with: Tomás, in a
    // household that set none up, was told to run the enrollment "on one of them".
    [Fact]
    public async Task AHouseholdWithNoVoiceServer_IsShownNoVoiceEnrollment_UntilItSetsOneUp()
    {
        await using var server = await AccountsServer.StartAsync();
        var (casa, ines) = await server.SeedHouseholdAsync("Casa Rivas", "Inés");
        var session = await server.MintSessionAsync(ines);

        var without = await server.SignedIn(session).PageAsync(Account);
        await server.MintTunnelCredentialAsync(casa, "voice");
        var with = await server.SignedIn(session).PageAsync(Account);

        without.ShouldNotContain("scripts/enroll-voice.sh");
        without.ShouldContain("no voice satellites");
        with.ShouldContain($"scripts/enroll-voice.sh {ines}");
    }

    // The role was the role's constant, so a member read "member of the household." under their name.
    [Fact]
    public async Task ThePage_SaysWhichHouseholdThePersonIsIn_AsASentence()
    {
        await using var server = await AccountsServer.StartAsync();
        var (casa, _) = await server.SeedHouseholdAsync("Casa Rivas", "Inés");
        var tomas = await server.SeedPersonAsync(casa, "Tomás");

        var page = await server.SignedIn(await server.MintSessionAsync(tomas)).PageAsync(Account);

        page.ShouldContain("A member of Casa Rivas.");
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
        (await server.Admin(await server.MintSessionAsync(fran.UserId))
                .PutAsync(AdminProtocol.Member(laura), new MemberEdit(SeesAll: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await server.SignedIn(await server.MintSessionAsync(pablo)).PageAsync(Account);

        var start = page.IndexOf("data-sees-all", StringComparison.Ordinal);
        var holders = page[start..page.IndexOf("</section>", start, StringComparison.Ordinal)];
        holders.ShouldContain("Laura");
        holders.ShouldNotContain("Fran");
        holders.ShouldNotContain("Pablo");
    }

    // A Sugary log is a health record: everyone in the household can see whose agent may read it.
    [Fact]
    public async Task ThePage_NamesWhoReadsEveryonesSugaryLog()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var pablo = await server.SeedMemberAsync("Pablo");
        (await server.Admin(await server.MintSessionAsync(fran.UserId))
                .PutAsync(AdminProtocol.Member(laura), new MemberEdit(ReadsSugaryLogs: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await server.SignedIn(await server.MintSessionAsync(pablo)).PageAsync(Account);

        var start = page.IndexOf("data-reads-sugary-logs", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var holders = page[start..page.IndexOf("</section>", start, StringComparison.Ordinal)];
        holders.ShouldContain("Laura");
        holders.ShouldNotContain("Fran");
        holders.ShouldNotContain("Pablo");
    }

    // A person's name and picture are their own to set, and every app asking who is signed in shows
    // what they set.
    [Fact]
    public async Task APersonsOwnNameAndAvatar_AreWhatTheAppsShow()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var browser = server.SignedIn(await server.MintSessionAsync(laura));

        var response = await browser.PostAsync(Account, $"{Account}/profile",
            ("displayName", "  Laura G.  "), ("avatarUrl", "/avatars/kakera.png"));

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var me = await MeAsync(browser);
        (me["displayName"]!.GetValue<string>(), me["avatarUrl"]!.GetValue<string>()).ShouldBe(("Laura G.", "/avatars/kakera.png"));
        var page = await browser.PageAsync(Account);
        page.ShouldContain("value=\"Laura G.\"");
        page.ShouldContain("value=\"/avatars/kakera.png\"");
    }

    [Fact]
    public async Task AnEmptyAvatar_TakesThePictureAway()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var browser = server.SignedIn(await server.MintSessionAsync(laura));
        (await browser.PostAsync(Account, $"{Account}/profile", ("displayName", "Laura"), ("avatarUrl", "https://example.com/l.png")))
            .StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        (await browser.PostAsync(Account, $"{Account}/profile", ("displayName", "Laura"), ("avatarUrl", " ")))
            .StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        (await MeAsync(browser))["avatarUrl"].ShouldBeNull();
    }

    // An avatar is drawn as an image on every app's page, so it is a web address or a path on the
    // deployment's own hosts; anything else — a script, another scheme, a host-relative "//" that a
    // browser reads as somebody else's host — is refused and nothing changes.
    [Theory]
    [InlineData("Laura", "javascript:alert(1)")]
    [InlineData("Laura", "//evil.example/l.png")]
    [InlineData("Laura", "data:image/png;base64,AAAA")]
    [InlineData("Laura", "ftp://example.com/l.png")]
    [InlineData("Laura", "avatars/l.png")]
    [InlineData("   ", "")]
    public async Task ANameOrAvatarThatIsNotOne_IsRefused(string displayName, string avatarUrl)
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var browser = server.SignedIn(await server.MintSessionAsync(laura));

        var response = await browser.PostAsync(Account, $"{Account}/profile", ("displayName", displayName), ("avatarUrl", avatarUrl));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var me = await MeAsync(browser);
        (me["displayName"]!.GetValue<string>(), me["avatarUrl"]).ShouldBe(("Laura", null));
    }

    [Fact]
    public async Task AProfilePostWithoutTheFormsToken_IsRefused()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var browser = server.SignedIn(await server.MintSessionAsync(laura));

        var response = await browser.PostWithoutPageAsync($"{Account}/profile", ("displayName", "Mallory"), ("avatarUrl", ""));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await MeAsync(browser))["displayName"]!.GetValue<string>().ShouldBe("Laura");
    }

    private static async Task<System.Text.Json.Nodes.JsonObject> MeAsync(AccountsServer.PageClient browser)
    {
        var response = await browser.GetAsync("/accounts/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
}