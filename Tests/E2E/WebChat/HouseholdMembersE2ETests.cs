using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The admin area's members page in the chat client (seam 2 of Tenants 4c): an owner invites somebody
// and is shown the link once, changes a member's sees-all, disables a member — who is signed out at
// once — re-enables them and mints them a recovery link that opens their enrollment.
//
// Every person here is made for the test in the stack's second household (PersonElsewhereAsync), not
// the home: a role, sees-all or a member disabled has every open page of the household renew its
// connection, which in the home caught other collections' dictations and gestures mid-way. Only they
// are changed, never anybody seeded. The members page is opened directly; the way in from the chat is
// HouseholdAdminE2ETests'. What a disabled member is to Telegram, voice and the
// agent is the admin API's seam (HouseholdMembersAdminTests) and the monitor's (ChatMonitorAssertionTests).
// The members page's helpers, shared by the classes that drive it; no test of its own, since a test
// inherited by two classes runs in both.
public abstract partial class HouseholdMembersE2EBase(WebChatE2EFixture fixture)
{
    protected WebChatE2EFixture Fixture => fixture;

    protected async Task<IPage> MembersPageAsync(string ownerSession) =>
        await MembersPageAsync(await fixture.CreatePageSignedInAsync(ownerSession));

    protected async Task<IPage> MembersPageAsync(IPage page)
    {
        await page.GotoAsync(fixture.DefaultSpaceUrl + "admin/members");
        await page.WaitForURLAsync(new Regex("/admin/members$"));
        // The area boots the app before it draws its menu.
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Members" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        return page;
    }

    // The fixture keeps one page of its own at a time, and two owners need two at once.
    protected async Task<IBrowserContext> ContextOfAsync(IPage beside, string session)
    {
        var context = await beside.Context.Browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(
        [
            new Microsoft.Playwright.Cookie
            {
                Name = WebChatE2EFixture.SessionCookieName, Value = session, Url = fixture.WebChatUrl,
                HttpOnly = true, SameSite = SameSiteAttribute.Lax
            }
        ]);
        return context;
    }

    // Who a session is, as the chat client asks on loading: 401 once it is ended. Asked apart from the
    // owner's browser, which holds one page at a time and the owner's cookie.
    protected async Task<HttpStatusCode> MeAsync(string session)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(fixture.WebChatUrl), "/accounts/me"));
        request.Headers.Add("Cookie", $"{WebChatE2EFixture.SessionCookieName}={session}");
        request.Headers.Add("Accept", "application/json");
        return (await client.SendAsync(request)).StatusCode;
    }

    // The link opens the enrollment page for the person it names, in a tab beside the owner's.
    protected static async Task ExpectEnrollmentForAsync(IPage owner, string? link, string name)
    {
        var tab = await owner.Context.NewPageAsync();
        await tab.GotoAsync(link.ShouldNotBeNull());
        await Assertions.Expect(tab.Locator("body")).ToContainTextAsync(name);
        await tab.CloseAsync();
    }

    protected async Task<string> SessionOfAsync(string name, string role)
    {
        return (await fixture.PersonElsewhereAsync(name, role)).Session;
    }


    [GeneratedRegex(@"/accounts/enroll/[A-Za-z0-9_-]+$")]
    protected static partial Regex Enrollment();
}

[Collection(WebChatE2ECollections.Members)]
[Trait("Category", "E2E")]
public class HouseholdMembersE2ETests(WebChatE2EFixture fixture) : HouseholdMembersE2EBase(fixture)
{
    [SkippableFact]
    public async Task AnOwner_InvitesSomebody_AndIsShownTheLinkOnce()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await MembersPageAsync(await SessionOfAsync($"Owner {tag}", "owner"));

        await page.Locator("input[name=inviteName]").FillAsync($"Invitee {tag}", new LocatorFillOptions { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create invite" }).ClickAsync();

        var minted = page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New link" });
        await Assertions.Expect(minted).ToContainTextAsync($"An invite for Invitee {tag}");
        var link = await minted.Locator("[data-member-link]").GetAttributeAsync("href");
        link.ShouldNotBeNull().ShouldMatch(Enrollment().ToString());
        await Assertions.Expect(minted.Locator("[data-telegram-start], [data-telegram-link]")).ToHaveCountAsync(1);
        await ExpectEnrollmentForAsync(page, link, $"Invitee {tag}");

        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Invite someone" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New link" })).ToHaveCountAsync(0);
    }

    // A Telegram group is bound from here as well: this is the one members page.
    [SkippableFact]
    public async Task AnOwner_MintsATelegramGroupCode_ShownOnceAsTheCommandToSend()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var page = await MembersPageAsync(await SessionOfAsync($"Owner {tag}", "owner"));

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Bind a Telegram group" })
            .ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        var minted = page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New group code" });
        await Assertions.Expect(minted.Locator("code")).ToHaveTextAsync(new Regex(@"^/bind [A-Za-z0-9_-]{20,}$"));
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Telegram groups" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New group code" })).ToHaveCountAsync(0);
    }

    [SkippableFact]
    public async Task AnOwner_DisablesAMember_WhoIsSignedOut_ReEnablesThem_AndMintsThemARecoveryLink()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var name = $"Member {tag}";
        var memberSession = await SessionOfAsync(name, "member");
        (await MeAsync(memberSession)).ShouldBe(HttpStatusCode.OK);
        var page = await MembersPageAsync(await SessionOfAsync($"Owner {tag}", "owner"));
        var row = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await Assertions.Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        // A one-time schedule of theirs due while they are disabled is refused and gone by the time they
        // are re-enabled: the page says so rather than promise that it waits.
        await Assertions.Expect(page.Locator(".admin-lede").First).ToContainTextAsync("a one-time schedule of theirs due meanwhile never runs");

        await row.GetByLabel("Sees all conversations").CheckAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Disable", Exact = true }).ClickAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Disable {name}" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
        await Assertions.Expect(row).ToContainTextAsync("disabled");
        await Assertions.Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Recovery link" })).ToHaveCountAsync(0);
        (await MeAsync(memberSession)).ShouldBe(HttpStatusCode.Unauthorized);

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Re-enable" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
        await Assertions.Expect(row).Not.ToContainTextAsync("disabled");
        (await MeAsync(memberSession)).ShouldBe(HttpStatusCode.Unauthorized, "a re-enabled member signs in again");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Recovery link" }).ClickAsync();
        var minted = page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New link" });
        await Assertions.Expect(minted).ToContainTextAsync($"A recovery link for {name}");
        await ExpectEnrollmentForAsync(page, await minted.Locator("[data-member-link]").GetAttributeAsync("href"), name);

        await page.ReloadAsync();
        row = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await Assertions.Expect(row.GetByLabel("Sees all conversations")).ToBeCheckedAsync(new LocatorAssertionsToBeCheckedOptions { Timeout = 30_000 });
    }
}