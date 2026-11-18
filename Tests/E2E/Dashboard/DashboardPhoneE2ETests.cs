using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

// The dashboard on a phone: nothing runs off the side of the page, and every page is reached by its
// name. Voice's sixteen metrics were a row of pills wider than the screen, and the bottom bar was
// eleven bare icons.
[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardPhoneE2ETests(DashboardE2EFixture fixture)
{
    // Whatever is wider than the screen outside a table, which scrolls inside its own card.
    private const string Overflowing = """
        () => {
            const width = document.documentElement.clientWidth;
            const wide = [...document.querySelectorAll('.content, .content *')]
                .filter(el => !el.closest('.events-table, .apexcharts-canvas, .pill-group, .tab-bar'))
                .filter(el => el.getBoundingClientRect().right > width + 1)
                .map(el => el.tagName.toLowerCase() + '.' + [...el.classList].join('.'));
            const content = document.querySelector('.content');
            if (content.scrollWidth > content.clientWidth + 1) wide.push('content scrolls sideways');
            if (document.documentElement.scrollWidth > width + 1) wide.push('page scrolls sideways');
            return wide.slice(0, 5);
        }
        """;

    private static readonly (string Path, string Title)[] _pages =
    [
        ("/tokens", "Token Usage"),
        ("/memory", "Memory"),
        ("/voice", "Voice"),
        ("/web", "Web")
    ];

    // The overview opened at its address, then each other page reached inside the same app: a phone
    // page apiece booted the app five times over to measure five widths.
    [Fact]
    public async Task EveryPage_FitsAPhonesWidth()
    {
        var page = await fixture.CreatePageAsync(hasTouch: true, isMobile: true);
        await page.GotoAsync(fixture.DashboardUrl);
        await Assertions.Expect(page.Locator("h2")).ToContainTextAsync("Overview", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        var overflowing = new List<string>();
        overflowing.AddRange((await page.EvaluateAsync<string[]>(Overflowing)).Select(el => $"/: {el}"));

        foreach (var (path, title) in _pages)
        {
            await page.EvaluateAsync("path => Blazor.navigateTo(path)", path);
            await page.WaitForURLAsync($"**{path}");
            await Assertions.Expect(page.Locator("h2")).ToContainTextAsync(title);
            overflowing.AddRange((await page.EvaluateAsync<string[]>(Overflowing)).Select(el => $"{path}: {el}"));
        }

        overflowing.ShouldBeEmpty();
    }

    [Fact]
    public async Task EveryPage_IsReachedByItsName()
    {
        var page = await fixture.CreatePageAsync(hasTouch: true, isMobile: true);
        await page.GotoAsync(fixture.DashboardUrl);

        var tokens = page.Locator("nav.sidebar a[href='/tokens']");
        await Assertions.Expect(tokens).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        await Assertions.Expect(tokens).ToHaveTextAsync("Tokens");

        var voice = page.Locator("nav.sidebar a[href='/voice']");
        await Assertions.Expect(voice).ToBeHiddenAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "More" }).ClickAsync();
        await Assertions.Expect(voice).ToBeVisibleAsync();
        await Assertions.Expect(voice).ToHaveTextAsync("Voice");

        await voice.ClickAsync();
        await page.WaitForURLAsync("**/voice");
        await Assertions.Expect(voice).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator("h2")).ToContainTextAsync("Voice");
    }
}