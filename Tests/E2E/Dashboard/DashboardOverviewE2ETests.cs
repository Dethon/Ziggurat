using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardOverviewE2ETests(DashboardE2EFixture fixture)
{
    [Fact]
    public async Task LoadOverview_ShowsKpiCardsHealthGridAndConnectionStatus()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var kpiCards = page.Locator(".kpi-card");
        var count = await kpiCards.CountAsync();
        count.ShouldBe(7);

        var labels = await kpiCards.Locator(".kpi-label").AllTextContentsAsync();
        labels.ShouldContain("Input Tokens");
        labels.ShouldContain("Output Tokens");
        labels.ShouldContain("Cost");
        labels.ShouldContain("Tool Calls");
        labels.ShouldContain("Errors");

        var healthGrid = page.Locator(".health-grid");
        (await healthGrid.CountAsync()).ShouldBeGreaterThan(0);

        var status = page.Locator(".connection-status");
        await status.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        (await status.IsVisibleAsync()).ShouldBeTrue();
    }
}