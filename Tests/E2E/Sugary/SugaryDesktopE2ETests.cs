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
    public async Task OnADesktop_TheTabsAreARailBesideThePage_TheActionSitsBesideIt_AndSheetsOpenCentred()
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
        // The window less its scrollbar, whose room a desktop keeps even on a short page.
        var shown = await page.EvaluateAsync<float>("() => document.body.getBoundingClientRect().width");
        (panel.X + panel.Width / 2).ShouldBe(shown / 2, 2, "a sheet sits in the middle of the window");
        panel.Y.ShouldBeGreaterThan(24, "with room above it as below");
    }

    // A wide window (a large monitor, a laptop from 1440 px) left most of itself empty around a page
    // drawn for 1024. There the meal she is building stands in a column beside her log, the two one
    // entry in the rail, and a meal she saves joins the log already on screen beside it, unreloaded.
    [SkippableFact]
    public async Task OnAWideWindow_TheMealStandsBesideTheLog_InOneRailEntry_AndASavedMealJoinsIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        var person = await fixture.AccountForAsync(fixture.NextUserIndex());
        var browser = (await fixture.CreatePageAsync()).Context.Browser!;
        await using var wide = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ExtraHTTPHeaders = Headers(person)
        });
        var page = await wide.NewPageAsync();
        await page.GotoAsync(fixture.SugaryUrl);
        var tabs = page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Sugary" });
        await Assertions.Expect(tabs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Meal & log" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        await Assertions.Expect(tabs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Meal", Exact = true }))
            .ToHaveCountAsync(0);

        var meal = await BoxOfAsync(page.Locator(".meal-column"));
        var log = await BoxOfAsync(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Log", Exact = true }));
        (meal.X + meal.Width).ShouldBeLessThanOrEqualTo(log.X, "the log stands beside the meal, not under it");
        meal.Height.ShouldBe(1080, 1, "the meal is a column the window's whole height");

        var name = $"Torrija {Guid.NewGuid():N}"[..16];
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "By hand" }).ClickAsync();
        var sheet = page.Locator("dialog[open]");
        await sheet.Locator("input").Nth(0).FillAsync(name, new LocatorFillOptions { Timeout = 30_000 });
        await sheet.Locator("input").Nth(1).FillAsync("30");
        await sheet.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add to meal" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save to log" }).ClickAsync();

        await Assertions.Expect(page.Locator(".panes-log").GetByText(name))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".meal-column").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take a photo" }))
            .ToBeVisibleAsync();
    }

    // A sheet closes on a click on the scrim around it. Selecting a field's words with the mouse and
    // letting go past the sheet's edge (an everyday overshoot beside a centred sheet) also ends in a
    // click on the scrim's element, and it closed the sheet with everything typed in it.
    [SkippableFact]
    public async Task SelectingAFieldsWordsPastTheSheetsEdge_KeepsTheSheetOpen()
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
        await page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Sugary" })
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Pantry" })
            .ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New food" }).ClickAsync();
        var sheet = page.Locator("dialog[open]");
        var name = sheet.Locator("input").First;
        await name.FillAsync("Bizcocho de limón", new LocatorFillOptions { Timeout = 30_000 });

        var field = await BoxOfAsync(name);
        var y = field.Y + field.Height / 2;
        await page.Mouse.MoveAsync(field.X + field.Width - 8, y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(40, y, new MouseMoveOptions { Steps = 5 });
        await page.Mouse.UpAsync();

        await Assertions.Expect(sheet).ToHaveCountAsync(1);
        await Assertions.Expect(name).ToHaveValueAsync("Bizcocho de limón");

        await page.Mouse.ClickAsync(40, y);
        await Assertions.Expect(sheet).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
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