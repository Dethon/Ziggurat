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

    [Theory]
    [InlineData("/", "Overview")]
    [InlineData("/tokens", "Token Usage")]
    [InlineData("/memory", "Memory")]
    [InlineData("/voice", "Voice")]
    [InlineData("/web", "Web")]
    public async Task APage_FitsAPhonesWidth(string path, string title)
    {
        var page = await fixture.CreatePageAsync(hasTouch: true, isMobile: true);
        await page.GotoAsync(fixture.DashboardUrl.TrimEnd('/') + path);
        await Assertions.Expect(page.Locator("h2")).ToContainTextAsync(title, new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });

        (await page.EvaluateAsync<string[]>(Overflowing)).ShouldBeEmpty();
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