using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Sugary;

// The household's pantry, edited by two people at once in the app: Ana raised a recipe's flour and
// saved, then Dani — whose sheet had opened before — changed its servings and saved, and Ana's flour
// was back to what it was, the carbs per helping wrong with nobody told. Each sheet now sends what it
// opened with, and only what it changed is laid over the recipe. No model is asked.
[Collection(WebChatE2ECollections.Sugary)]
[Trait("Category", "E2E")]
public class SugaryPantryE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task TwoPeopleEditingOneRecipe_EachChangeStands()
    {
        Skip.If(string.IsNullOrEmpty(fixture.SugaryUrl), "Sugary stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var ana = await fixture.AccountForAsync(fixture.NextUserIndex());
        var dani = await fixture.AccountForAsync(fixture.NextUserIndex());
        ana.TenantId.ShouldBe(dani.TenantId, "the pantry is the household's, so both must be of one");

        using var api = new HttpClient { BaseAddress = new Uri(fixture.SugaryUrl) };
        foreach (var (name, value) in Headers(ana))
        {
            api.DefaultRequestHeaders.Add(name, value);
        }

        var flour = await IdOfAsync(await api.PostAsJsonAsync("/api/foods", new JsonObject
        {
            ["name"] = $"Harina {tag}", ["carbsPer100g"] = 72, ["unit"] = "g", ["source"] = "manual"
        }));
        var sugar = await IdOfAsync(await api.PostAsJsonAsync("/api/foods", new JsonObject
        {
            ["name"] = $"Azúcar {tag}", ["carbsPer100g"] = 100, ["unit"] = "g", ["source"] = "manual"
        }));
        var recipe = await IdOfAsync(await api.PostAsJsonAsync("/api/recipes", new JsonObject
        {
            ["name"] = $"Bizcocho {tag}",
            ["servings"] = 8,
            ["ingredients"] = new JsonArray(
                new JsonObject { ["foodId"] = flour, ["amount"] = 250 },
                new JsonObject { ["foodId"] = sugar, ["amount"] = 150 })
        }));

        var browser = (await fixture.CreatePageAsync()).Context.Browser!;
        await using var anasPhone = await PhoneAsync(browser, ana);
        await using var danisPhone = await PhoneAsync(browser, dani);
        var anas = await RecipeSheetAsync(anasPhone, $"Bizcocho {tag}");
        var danis = await RecipeSheetAsync(danisPhone, $"Bizcocho {tag}");

        await anas.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = $"Harina {tag}" }).FillAsync("300");
        await anas.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save recipe" }).ClickAsync();
        await Assertions.Expect(anas.Locator("dialog")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        await danis.Locator("dialog input").Last.FillAsync("10");
        await danis.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save recipe" }).ClickAsync();
        await Assertions.Expect(danis.Locator("dialog")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        var saved = (await api.GetFromJsonAsync<JsonArray>("/api/recipes"))!
            .Single(r => r!["id"]!.GetValue<string>() == recipe)!;
        saved["servings"]!.GetValue<double>().ShouldBe(10);
        saved["ingredients"]!.AsArray().Select(i => i!["amount"]!.GetValue<double>()).ShouldBe([300d, 150d],
            "Dani's save put back the flour her sheet had loaded");
    }

    // A phone signed in as this person, as Sugary's proxy hands its requests on; no service worker,
    // which would answer the app's files out of the headers' sight.
    private async Task<IBrowserContext> PhoneAsync(IBrowser browser, Account person)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            ExtraHTTPHeaders = Headers(person)
        });
        return context;
    }

    private async Task<IPage> RecipeSheetAsync(IBrowserContext phone, string recipe)
    {
        var page = await phone.NewPageAsync();
        await page.GotoAsync(fixture.SugaryUrl);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Pantry", Exact = true }).Last
            .ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
        await page.GetByText(recipe).First.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save recipe" }))
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

    private static async Task<string> IdOfAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();
    }
}