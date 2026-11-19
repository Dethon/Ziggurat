using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sugary;

// An owner lets a member read everyone's Sugary log on the members page, and the member's app offers
// whose log to show: picking her shows her meal, with nothing to add or change. Its people are the
// stack's second household's (PersonElsewhereAsync): a grant has every open page of a household renew
// its connection. No model is asked.
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugaryViewingE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AReaderAnOwnerGranted_PicksHerLog_AndSeesHerMealReadOnly()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        var tag = Guid.NewGuid().ToString("N")[..6];
        var (_, ownerSession) = await fixture.PersonElsewhereAsync($"Owner-{tag}", "owner");
        var (readerId, _) = await fixture.PersonElsewhereAsync($"Reader-{tag}", "member");
        var (herId, _) = await fixture.PersonElsewhereAsync($"Her-{tag}", "member");
        var reader = new Account(fixture.ElsewhereHousehold, readerId);
        var her = new Account(fixture.ElsewhereHousehold, herId);

        var browser = (await fixture.CreatePageAsync()).Context.Browser!;
        await using (var ownerContext = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true }))
        {
            await ownerContext.AddCookiesAsync(
            [
                new Microsoft.Playwright.Cookie
                {
                    Name = WebChatE2EFixture.SessionCookieName, Value = ownerSession, Url = fixture.WebChatUrl,
                    HttpOnly = true, SameSite = SameSiteAttribute.Lax
                }
            ]);
            var owner = await ownerContext.NewPageAsync();
            await owner.GotoAsync(fixture.DefaultSpaceUrl + "admin/members");
            await owner.WaitForURLAsync(new Regex("/admin/members$"));
            var row = owner.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = $"Reader-{tag}" });
            await row.GetByLabel("Reads everyone's Sugary log").SetCheckedAsync(true, new LocatorSetCheckedOptions { Timeout = 60_000 });
            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
            await Assertions.Expect(owner.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
        }

        using (var api = new HttpClient { BaseAddress = new Uri(fixture.SugaryUrl) })
        {
            foreach (var (name, value) in Headers(her))
            {
                api.DefaultRequestHeaders.Add(name, value);
            }

            var saved = await api.PostAsJsonAsync("/api/meals", new JsonObject
            {
                ["eatenAt"] = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O"),
                ["notes"] = $"lentejas {tag}",
                ["outcome"] = "unknown",
                ["items"] = new JsonArray(new JsonObject
                {
                    ["name"] = $"lentejas {tag}", ["grams"] = 250, ["carbsPer100g"] = 16, ["carbsLow"] = 35, ["carbs"] = 40,
                    ["carbsHigh"] = 45, ["source"] = "manual"
                })
            });
            saved.StatusCode.ShouldBe(HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        }

        await using var phone = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ExtraHTTPHeaders = Headers(reader)
        });
        var page = await phone.NewPageAsync();
        await page.GotoAsync(fixture.SugaryUrl);
        var picker = page.GetByLabel("Whose log");

        await picker.SelectOptionAsync(new SelectOptionValue { Label = $"Her-{tag}" }, new LocatorSelectOptionOptions { Timeout = 60_000 });

        await Assertions.Expect(page.GetByText($"You're looking at Her-{tag}'s log.")).ToBeVisibleAsync();
        var tabs = page.GetByRole(AriaRole.Navigation, new PageGetByRoleOptions { Name = "Sugary" });
        await tabs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Log" }).ClickAsync();
        await Assertions.Expect(page.GetByText($"lentejas {tag}").First).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(tabs.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Meal" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("Add to your log")).ToHaveCountAsync(0);
    }

    private static Dictionary<string, string> Headers(Account person) => new()
    {
        [ForwardedAccount.UserHeader] = person.UserId,
        [ForwardedAccount.TenantHeader] = person.TenantId,
        [ForwardedAccount.RolesHeader] = "member",
        [ForwardedAccount.AssertionHeader] = TestAssertions.Of(person, SugaryChannel.ChannelId)
    };
}