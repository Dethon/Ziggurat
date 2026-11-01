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

    // Suspended from its row, the household's people are told so wherever the deployment's check stands
    // — here the dashboard's own host, which would otherwise refuse them for not being the operator — and
    // resumed from the same row, they are only that again. The home has no button: it is the operator's.
    [Fact]
    public async Task TheOperator_SuspendsAHouseholdFromItsRow_ItsPeopleAreToldSo_AndResumesIt()
    {
        var (page, _) = await fixture.CreateOperatorPageAsync();
        await OpenAsync(page);
        await page.GetByLabel("Household name").FillAsync("Casa Gil");
        await page.GetByLabel("Owner's name").FillAsync("Eva");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create household" }).ClickAsync();
        await WaitForLinkAsync(page);
        var row = Row(page, "Casa Gil");
        var eva = await fixture.OwnerSessionAsync(page, "Casa Gil");
        var suspend = row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Suspend Casa Gil" });
        await Assertions.Expect(suspend).ToBeVisibleAsync();
        (await Row(page, "Home").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Suspend Home" }).CountAsync())
            .ShouldBe(0, "the home is the operator's own");

        await suspend.ClickAsync();

        await Assertions.Expect(row).ToContainTextAsync("suspended since");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        var (status, body) = await fixture.OpenAsAsync(eva);
        status.ShouldBe(HttpStatusCode.Forbidden);
        body.ShouldContain("Household suspended");
        body.ShouldContain(global::Accounts.Sessions.SessionCheck.SuspendedNotice);

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resume Casa Gil" }).ClickAsync();

        await Assertions.Expect(row).ToContainTextAsync("running");
        var (resumedStatus, resumedBody) = await fixture.OpenAsAsync(eva);
        resumedStatus.ShouldBe(HttpStatusCode.Forbidden);
        resumedBody.ShouldNotContain("Household suspended", customMessage: "resumed, she is refused only for not being the operator");
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