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
        var enrollment = await fixture.CreatePageAsync();
        await enrollment.GotoAsync(link!);
        await Assertions.Expect(enrollment.Locator("body")).ToContainTextAsync($"Invitee {tag}");

        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Invite someone" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New link" })).ToHaveCountAsync(0);
    }

    [SkippableFact]
    public async Task AnOwner_DisablesAMember_WhoIsSignedOut_ReEnablesThem_AndMintsThemARecoveryLink()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var name = $"Member {tag}";
        var memberSession = await SessionOfAsync(name, "member");
        var member = await fixture.CreatePageSignedInAsync(memberSession);
        (await MeAsync(member)).ShouldBe(200);
        var page = await MembersPageAsync(await SessionOfAsync($"Owner {tag}", "owner"));
        var row = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await Assertions.Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        await row.GetByLabel("Sees all conversations").CheckAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Disable", Exact = true }).ClickAsync();
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Disable {name}" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
        await Assertions.Expect(row).ToContainTextAsync("disabled");
        await Assertions.Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Recovery link" })).ToHaveCountAsync(0);
        (await MeAsync(member)).ShouldBe(401);

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Re-enable" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
        await Assertions.Expect(row).Not.ToContainTextAsync("disabled");
        (await MeAsync(member)).ShouldBe(401);

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Recovery link" }).ClickAsync();
        var minted = page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New link" });
        await Assertions.Expect(minted).ToContainTextAsync($"A recovery link for {name}");
        var recovery = await fixture.CreatePageAsync();
        await recovery.GotoAsync((await minted.Locator("[data-member-link]").GetAttributeAsync("href"))!);
        await Assertions.Expect(recovery.Locator("body")).ToContainTextAsync(name);

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

    // Who the session the page carries is, as the chat client asks on loading: 401 once it is ended.
    private async Task<int> MeAsync(IPage page) =>
        (await page.APIRequest.GetAsync(new Uri(new Uri(fixture.WebChatUrl), "/accounts/me").ToString())).Status;

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