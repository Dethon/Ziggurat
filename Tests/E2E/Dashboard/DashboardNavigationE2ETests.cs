using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardNavigationE2ETests(DashboardE2EFixture fixture)
{
    [Theory]
    [InlineData("/tokens", "Token Usage")]
    [InlineData("/tools", "Tool Calls")]
    [InlineData("/errors", "Errors")]
    [InlineData("/schedules", "Schedule Executions")]
    [InlineData("/memory", "Memory")]
    [InlineData("/latency", "Latency")]
    [InlineData("/voice", "Voice")]
    public async Task NavigateToPage_ShowsCorrectPage(string href, string expectedTitle)
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // NetworkIdle says the network went quiet, not that Blazor has rendered. The sidebar comes
        // from MainLayout, so it exists only once the WASM app has booted, and a click that lands
        // before that spends its whole default timeout waiting for a link that is not there yet —
        // thirty seconds, reported as this page failing to navigate. On a quiet machine the boot
        // beats the click every time, which is why it took a loaded run to surface.
        var navLink = page.Locator($"nav.sidebar a[href='{href}']");
        await navLink.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await navLink.ClickAsync();

        await page.WaitForURLAsync($"**{href}");

        var header = page.Locator("h2");
        await Assertions.Expect(header).ToContainTextAsync(expectedTitle);
    }
}