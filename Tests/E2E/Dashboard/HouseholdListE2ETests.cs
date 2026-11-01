using System.Net;
using Domain.DTOs.Tunnel;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

// The operator's household list in the dashboard (seam 3 of Tenants 4c), through the dashboard's own
// host: the operator creates a household and is shown its owner's link once, sees each household's
// people, connected servers and last activity, and mints an owner's recovery link; nobody else is let in.
[Collection("DashboardAdminE2E")]
[Trait("Category", "E2E")]
public class HouseholdListE2ETests(DashboardAdminE2EFixture fixture)
{
    private const string LinkField = "Owner's passkey link";

    [Fact]
    public async Task TheOperator_CreatesAHousehold_AndIsShownItsOwnersLinkOnce()
    {
        var (page, home) = await fixture.CreateOperatorPageAsync();
        fixture.Connect(home, new ConnectedServer("vault", 1, DateTimeOffset.UtcNow.AddHours(-2), ["read_file"]));
        await OpenAsync(page);

        await page.GetByLabel("Household name").FillAsync("Casa Pérez");
        await page.GetByLabel("Owner's name").FillAsync("Ana");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create household" }).ClickAsync();

        var link = page.GetByLabel(LinkField);
        await Assertions.Expect(link).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(
            $"^{System.Text.RegularExpressions.Regex.Escape(fixture.DashboardUrl)}accounts/enroll/.+"));
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Ana");
        var casa = Row(page, "Casa Pérez");
        await Assertions.Expect(casa).ToContainTextAsync("Ana");
        await Assertions.Expect(casa).ToContainTextAsync("never");
        await Assertions.Expect(casa.GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("none");
        await Assertions.Expect(Row(page, "Home")).ToContainTextAsync("vault (since");
        await Assertions.Expect(Row(page, "Home")).ToContainTextAsync("Fran");

        await page.ReloadAsync();
        await Assertions.Expect(Row(page, "Casa Pérez")).ToBeVisibleAsync();
        (await page.GetByLabel(LinkField).CountAsync()).ShouldBe(0, "a link is shown once");
    }

    [Fact]
    public async Task TheOperator_MintsARecoveryLinkForAnOwner()
    {
        var (page, _) = await fixture.CreateOperatorPageAsync();
        await OpenAsync(page);
        await page.GetByLabel("Household name").FillAsync("Casa Ruiz");
        await page.GetByLabel("Owner's name").FillAsync("Bea");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create household" }).ClickAsync();
        var first = await WaitForLinkAsync(page);

        await Row(page, "Casa Ruiz").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Recovery link for Bea" })
            .ClickAsync();

        await Assertions.Expect(page.GetByLabel(LinkField)).Not.ToHaveValueAsync(first);
        (await page.GetByLabel(LinkField).InputValueAsync()).ShouldStartWith($"{fixture.DashboardUrl}accounts/enroll/");
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Bea");
    }

    // The dashboard's host lets in the operator's session alone: an owner of the home household is
    // refused before anything of the dashboard is served.
    [Fact]
    public async Task SomebodyWhoIsNotTheOperator_IsRefusedTheList()
    {
        var page = await fixture.CreatePersonPageAsync("Olga", "owner");

        var response = await page.GotoAsync($"{fixture.DashboardUrl}households");

        response!.Status.ShouldBe((int)HttpStatusCode.Forbidden);
    }

    private async Task OpenAsync(IPage page)
    {
        await page.GotoAsync($"{fixture.DashboardUrl}households");
        // The list is drawn once the WASM app has booted and asked the admin API.
        await Assertions.Expect(Row(page, "Home")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
    }

    private static async Task<string> WaitForLinkAsync(IPage page)
    {
        var link = page.GetByLabel(LinkField);
        await Assertions.Expect(link).ToBeVisibleAsync();
        return await link.InputValueAsync();
    }

    private static ILocator Row(IPage page, string household) =>
        page.GetByRole(AriaRole.Row, new PageGetByRoleOptions { Name = household, Exact = true });
}