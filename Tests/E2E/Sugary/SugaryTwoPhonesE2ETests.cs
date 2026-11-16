using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sugary;

// One person's two phones open on one insulin dose: one corrected its units and saved, then the
// other — whose sheet had opened before — added a note and saved, and the units were back to what
// that sheet had loaded, a dose she did not take on record with nobody told. Each sheet now sends
// what it opened with, and only what it changed is laid over the dose. No model is asked.
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugaryTwoPhonesE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task TwoPhonesEditingOneDose_EachChangeStands()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var eva = await fixture.AccountForAsync(fixture.NextUserIndex());
        using var api = new HttpClient { BaseAddress = new Uri(fixture.SugaryUrl) };
        foreach (var (name, value) in Headers(eva))
        {
            api.DefaultRequestHeaders.Add(name, value);
        }

        // Seconds past the minute, as a dose saved with one tap has: the sheet shows it to the minute.
        var takenAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        takenAt = takenAt.AddTicks(-(takenAt.Ticks % TimeSpan.TicksPerMinute)).AddSeconds(37);
        var created = await api.PostAsJsonAsync("/api/doses", new JsonObject
        {
            ["takenAt"] = takenAt.ToString("O", CultureInfo.InvariantCulture),
            ["units"] = 6,
            ["kind"] = "rapid",
            ["note"] = $"lunch {tag}"
        });
        created.EnsureSuccessStatusCode();
        var dose = (await created.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

        var browser = (await fixture.CreatePageAsync()).Context.Browser!;
        await using var phone = await PhoneAsync(browser, eva);
        await using var tablet = await PhoneAsync(browser, eva);
        var first = await DoseSheetAsync(phone, tag);
        var second = await DoseSheetAsync(tablet, tag);

        await first.GetByLabel("Units").FillAsync("7");
        await first.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save dose" }).ClickAsync();
        await Assertions.Expect(first.Locator("dialog")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await second.GetByLabel("Note").FillAsync($"lunch {tag}, then a walk");
        await second.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save dose" }).ClickAsync();
        await Assertions.Expect(second.Locator("dialog")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        var saved = (await api.GetFromJsonAsync<JsonArray>("/api/doses"))!
            .Single(d => d!["id"]!.GetValue<string>() == dose)!;
        saved["units"]!.GetValue<double>().ShouldBe(7, "the second sheet's save put back the units it had loaded");
        saved["note"]!.GetValue<string>().ShouldBe($"lunch {tag}, then a walk");
        DateTimeOffset.Parse(saved["takenAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(takenAt, "neither sheet changed when it was taken");
    }

    // A phone signed in as this person, as Sugary's proxy hands its requests on; no service worker,
    // which would answer the app's files out of the headers' sight.
    private static async Task<IBrowserContext> PhoneAsync(IBrowser browser, Account person) =>
        await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ExtraHTTPHeaders = Headers(person)
        });

    private async Task<IPage> DoseSheetAsync(IBrowserContext phone, string tag)
    {
        var page = await phone.NewPageAsync();
        await page.GotoAsync(fixture.SugaryUrl);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log", Exact = true }).Last
            .ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await page.Locator(".dose-row", new PageLocatorOptions { HasText = tag }).First
            .ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save dose" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        return page;
    }

    private static Dictionary<string, string> Headers(Account person) => new()
    {
        [ForwardedAccount.UserHeader] = person.UserId,
        [ForwardedAccount.TenantHeader] = person.TenantId,
        [ForwardedAccount.RolesHeader] = "member",
        [ForwardedAccount.AssertionHeader] = TestAssertions.Of(person, SugaryChannel.ChannelId)
    };
}