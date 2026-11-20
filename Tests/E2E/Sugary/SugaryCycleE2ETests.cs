using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sugary;

// A day of her cycle keeps her flow and how she felt, tapped in the day sheet and there again after a
// reload; the period reminder she turns on stays on. Against the real server: the app's codes and the
// server's must agree. No model is asked.
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugaryCycleE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task ADaysFlowAndSymptoms_AreKept_AndHerReminderStaysOn()
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
        await page.GotoAsync($"{fixture.SugaryUrl}/#cycle");
        var today = page.Locator(".calendar-day-today");
        await today.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });

        var sheet = page.Locator("dialog[open]");
        await sheet.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Medium" }).ClickAsync();
        await sheet.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cramps" }).ClickAsync();
        await sheet.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(sheet).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await page.ReloadAsync();
        await today.ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await Assertions.Expect(sheet.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Medium" }))
            .ToHaveAttributeAsync("aria-pressed", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });
        await Assertions.Expect(sheet.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cramps" }))
            .ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(sheet.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Headache" }))
            .ToHaveAttributeAsync("aria-pressed", "false");

        var status = await page.APIRequest.GetAsync($"{fixture.SugaryUrl}/api/cycle/reminder");
        Assert.True(status.Ok, await status.TextAsync());
        var turned = await page.APIRequest.PutAsync($"{fixture.SugaryUrl}/api/cycle/reminder",
            new APIRequestContextOptions { DataObject = new { on = true } });
        Assert.True(turned.Ok, await turned.TextAsync());
        var again = await page.APIRequest.GetAsync($"{fixture.SugaryUrl}/api/cycle/reminder");
        Assert.Contains("\"on\":true", await again.TextAsync());
    }

    private static Dictionary<string, string> Headers(Account person) => new()
    {
        [ForwardedAccount.UserHeader] = person.UserId,
        [ForwardedAccount.TenantHeader] = person.TenantId,
        [ForwardedAccount.RolesHeader] = "member",
        [ForwardedAccount.AssertionHeader] = TestAssertions.Of(person, SugaryChannel.ChannelId)
    };
}