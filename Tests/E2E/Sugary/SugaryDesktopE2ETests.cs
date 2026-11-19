using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sugary;

// Sugary was drawn for a phone alone: on a desktop browser it was a 560 px column in the middle of the
// screen with the phone's tab bar stretched along the bottom and every sheet rising from the bottom edge.
// From a laptop's width the tabs are a rail down the left, the pinned action sits beside it, a sheet
// opens in the middle of the window, and nothing scrolls sideways. No model is asked.
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugaryDesktopE2ETests(WebChatE2EFixture fixture)
{
    private const int Width = 1440;
    private const int Height = 900;

    [SkippableFact]
    public async Task OnADesktop_TheTabsAreARailBesideThePage_AndTheActionAndSheetsSitBesideIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        var person = await fixture.AccountForAsync(fixture.NextUserIndex());
        var browser = (await fixture.CreatePageAsync()).Context.Browser!;
        await using var desktop = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = Width, Height = Height },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ExtraHTTPHeaders = Headers(person)
        });
        var page = await desktop.NewPageAsync();
        await page.GotoAsync(fixture.SugaryUrl);
        var tabs = page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Sugary" });
        var pantry = tabs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Pantry" });
        await pantry.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });

        var rail = await BoxOfAsync(tabs);
        rail.X.ShouldBe(0, 1, "the tabs stand down the left edge");
        rail.Height.ShouldBe(Height, 1, "the tabs run the window's whole height");
        rail.Width.ShouldBeLessThan(320);

        var main = await BoxOfAsync(page.Locator("main"));
        main.X.ShouldBeGreaterThanOrEqualTo(rail.X + rail.Width - 1, "the page starts beside the tabs, never under them");
        (await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"))
            .ShouldBeTrue("nothing scrolls sideways");

        var action = await BoxOfAsync(page.Locator(".action-bar"));
        action.X.ShouldBeGreaterThanOrEqualTo(rail.X + rail.Width - 1, "the pinned action sits beside the tabs");
        (action.Y + action.Height).ShouldBe(Height, 1, "with no tab bar under it, the action sits on the bottom edge");

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New food" }).ClickAsync();
        var sheet = page.Locator("dialog[open]");
        await Assertions.Expect(sheet).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        var panel = await BoxOfAsync(sheet.Locator(".sheet-panel"));
        (Height - (panel.Y + panel.Height)).ShouldBeGreaterThan(24, "a sheet opens in the window, not from its bottom edge");
        (panel.X + panel.Width / 2).ShouldBe(Width / 2, 2, "a sheet sits in the middle of the window");
        panel.Y.ShouldBeGreaterThan(24, "with room above it as below");
    }

    private static async Task<LocatorBoundingBoxResult> BoxOfAsync(ILocator locator)
    {
        await Assertions.Expect(locator).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        return (await locator.BoundingBoxAsync())!;
    }

    private static Dictionary<string, string> Headers(Account person) => new()
    {
        [ForwardedAccount.UserHeader] = person.UserId,
        [ForwardedAccount.TenantHeader] = person.TenantId,
        [ForwardedAccount.RolesHeader] = "member",
        [ForwardedAccount.AssertionHeader] = TestAssertions.Of(person, SugaryChannel.ChannelId)
    };
}