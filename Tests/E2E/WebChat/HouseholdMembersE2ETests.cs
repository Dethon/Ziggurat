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
// Every person here is made for the test (create-user) in the home household: only they are changed,
// never the signed-in owner or anybody seeded. What a disabled member is to Telegram, voice and the
// agent is the admin API's seam (HouseholdMembersAdminTests) and the monitor's (ChatMonitorAssertionTests).
[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public partial class HouseholdMembersE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AnOwner_InvitesSomebody_AndIsShownTheLinkOnce()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

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
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

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
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

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

    private async Task<IPage> MembersPageAsync(string ownerSession)
    {
        var page = await fixture.CreatePageSignedInAsync(ownerSession);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await page.GetByLabel("Household settings").ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await page.WaitForURLAsync(new Regex("/admin/settings$"));
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Members" }).ClickAsync();
        await page.WaitForURLAsync(new Regex("/admin/members$"));
        return page;
    }

    // Who a session is, as the chat client asks on loading: 401 once it is ended. Asked apart from the
    // owner's browser, which holds one page at a time and the owner's cookie.
    private async Task<HttpStatusCode> MeAsync(string session)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(fixture.WebChatUrl), "/accounts/me"));
        request.Headers.Add("Cookie", $"{WebChatE2EFixture.SessionCookieName}={session}");
        request.Headers.Add("Accept", "application/json");
        return (await client.SendAsync(request)).StatusCode;
    }

    // The link opens the enrollment page for the person it names, in a tab beside the owner's.
    private static async Task ExpectEnrollmentForAsync(IPage owner, string? link, string name)
    {
        var tab = await owner.Context.NewPageAsync();
        await tab.GotoAsync(link.ShouldNotBeNull());
        await Assertions.Expect(tab.Locator("body")).ToContainTextAsync(name);
        await tab.CloseAsync();
    }

    private async Task<string> SessionOfAsync(string name, string role)
    {
        var (_, created) = await fixture.AccountsAsync("create-user", "--name", name, "--role", role);
        var user = WhoWasMade().Match(created).Groups["user"].Value;
        var (_, session) = await fixture.AccountsAsync("mint-session", "--user", user);
        return session.Trim();
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household")]
    private static partial Regex WhoWasMade();

    [GeneratedRegex(@"/accounts/enroll/[A-Za-z0-9_-]+$")]
    private static partial Regex Enrollment();
}