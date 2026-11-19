using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sugary;

// A pull-down on the phone reloads the page, and every reload opened the meal whatever tab she was on.
// The tab is in the address now, so the page comes back where she left it. No model is asked.
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugaryTabsE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AReload_StaysOnTheTabSheWasOn()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        var person = await fixture.AccountForAsync(fixture.NextUserIndex());
        var browser = (await fixture.CreatePageAsync()).Context.Browser!;
        await using var phone = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ExtraHTTPHeaders = Headers(person)
        });
        var page = await phone.NewPageAsync();
        await page.GotoAsync(fixture.SugaryUrl);
        var tabs = page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Sugary" });
        var glucose = tabs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Glucose" });

        await glucose.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await Assertions.Expect(glucose).ToHaveAttributeAsync("aria-current", "page");
        await page.ReloadAsync();

        await Assertions.Expect(glucose).ToHaveAttributeAsync("aria-current", "page",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });
        await Assertions.Expect(tabs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Meal" }))
            .Not.ToHaveAttributeAsync("aria-current", "page");
    }

    private static Dictionary<string, string> Headers(Account person) => new()
    {
        [ForwardedAccount.UserHeader] = person.UserId,
        [ForwardedAccount.TenantHeader] = person.TenantId,
        [ForwardedAccount.RolesHeader] = "member",
        [ForwardedAccount.AssertionHeader] = TestAssertions.Of(person, SugaryChannel.ChannelId)
    };
}