using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The household's limits on its apps' asks in the admin area (seam 2): an owner sets the household's limit
// and finds it saved after a reload. In the stack's second household, since a limit of the home's is read by
// every app turn of it; what a limit does to an ask is the origin's seam (AppAskCapsOverTheWireTests), and
// the merge, bounds and apps of the household the admin API's (HouseholdAppAsksAdminTests).
[Collection(WebChatE2ECollections.AdminPages)]
[Trait("Category", "E2E")]
public class HouseholdAppsE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AnOwner_SetsTheHouseholdsLimit_AndFindsItSaved()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..6];
        var (_, session) = await fixture.PersonElsewhereAsync($"Owner {tag}", "owner");
        var page = await fixture.CreatePageSignedInAsync(session);
        // A value of this run's, so an earlier run's save is never what the reload finds.
        var limit = (Random.Shared.Next(1, 9_999)).ToString();

        await page.GotoAsync(fixture.DefaultSpaceUrl + "admin/apps");
        var field = page.GetByRole(AriaRole.Spinbutton, new PageGetByRoleOptions { Name = "Asks per day, each app, whole household" });
        await Assertions.Expect(field).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        await field.FillAsync(limit);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save limits" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

        await page.ReloadAsync();

        await Assertions.Expect(field).ToHaveValueAsync(limit, new LocatorAssertionsToHaveValueOptions { Timeout = 60_000 });
    }
}