using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The admin area's servers page in the chat client (seam 2 of Tenants 4c): an owner adds a server by
// name and is shown its tunnel credential once, sees it listed offline — the stack's real agent is
// asked, and no tunnel of it is open — and revokes its credential, after which it is gone.
//
// The server is the test's own, named with its tag in the home household: no agent definition names
// it, so no turn is touched. A server connecting with the credential shown, and a revocation closing
// its tunnel, are the admin API's seam against the real relay (HouseholdServersAdminTests).
[Collection(WebChatE2ECollections.Admin)]
[Trait("Category", "E2E")]
public partial class HouseholdServersE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AnOwner_AddsAServer_IsShownItsCredentialOnce_AndRevokesIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..6];
        var name = $"e2e-{tag}";
        var page = await ServersPageAsync(await SessionOfAsync($"Owner {tag}", "owner"));

        await page.Locator("input[name=serverName]").FillAsync(name, new LocatorFillOptions { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Mint its credential" }).ClickAsync();

        var minted = page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New credential" });
        await Assertions.Expect(minted).ToContainTextAsync($"The tunnel credential for {name}");
        var credential = await minted.Locator("[data-server-credential]").TextContentAsync();
        credential.ShouldNotBeNull().Trim().Length.ShouldBeGreaterThan(40);
        var server = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = name });
        await Assertions.Expect(server.Locator("[data-server-state]")).ToHaveTextAsync("Offline");

        await page.ReloadAsync();
        await Assertions.Expect(server).ToContainTextAsync("Credential minted", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new PageGetByRoleOptions { Name = "New credential" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("body")).Not.ToContainTextAsync(credential.Trim());

        await server.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Revoke", Exact = true }).ClickAsync();
        await server.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Revoke the credential" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Revoked.");
        await Assertions.Expect(server).ToHaveCountAsync(0);
    }

    private async Task<IPage> ServersPageAsync(string ownerSession)
    {
        var page = await fixture.CreatePageSignedInAsync(ownerSession);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await page.GetByLabel("Household settings").ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await page.WaitForURLAsync(new Regex("/admin/settings$"));
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Servers" }).ClickAsync();
        await page.WaitForURLAsync(new Regex("/admin/servers$"));
        return page;
    }

    private async Task<string> SessionOfAsync(string name, string role)
    {
        return (await fixture.PersonAsync(name, role)).Session;
    }

}