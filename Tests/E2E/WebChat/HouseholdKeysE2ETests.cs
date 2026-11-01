using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs.Admin;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The admin area's keys page in the chat client (seam 2 of Tenants 4c), its keys put to a stub of
// their providers that the stack runs in process and never a real one.
//
// Every person this stack signs in is in the home household, whose turns every collection runs, so no
// key is saved here: a key the provider rejects is the one an owner submits, and it changes nothing.
// What a saved key, an unchecked one and a key failing in a turn do is the admin API's seam
// (KeysAdminTests). The home's agents run on the deployment's key, so its owners never land on the
// page; an owner whose household has none is shown here by the answer such a household's keys get.
[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public partial class HouseholdKeysE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AnOwner_SubmitsAKeyItsProviderRejects_AndIsToldWhy_AndNothingIsSaved()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var owner = $"Owner {Guid.NewGuid().ToString("N")[..4]}";
        var page = await fixture.CreatePageSignedInAsync(await OwnerSessionAsync(owner));
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await Assertions.Expect(page.Locator(".signed-in-person"))
            .ToHaveAttributeAsync("title", owner, new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
        // The home's owner stays in the chat: its agents run on the deployment's key.
        await Eventually.Settle();
        page.Url.ShouldNotContain("/admin/");

        await page.GetByLabel("Household settings").ClickAsync();
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Keys" }).ClickAsync();

        await page.WaitForURLAsync(new Regex("/admin/keys$"));
        var openRouter = page.Locator("form[aria-labelledby=admin-key-openrouter]");
        await Assertions.Expect(openRouter.Locator(".admin-key-status"))
            .ToHaveTextAsync("Not set", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await Assertions.Expect(openRouter).ToContainTextAsync("deployment's own key");

        await openRouter.Locator("input[name=openRouterKey]").FillAsync("sk-or-v1-a-key-nobody-issued-0123456789");
        await openRouter.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();

        await Assertions.Expect(openRouter.GetByRole(AriaRole.Alert))
            .ToHaveTextAsync("The OpenRouter key was not saved: OpenRouter refused it (401).");
        await page.ReloadAsync();
        await Assertions.Expect(openRouter.Locator(".admin-key-status"))
            .ToHaveTextAsync("Not set", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
    }

    // A household with no OpenRouter key of its own: its agents refuse every turn until it has one, so
    // its owner is taken to the keys page as the chat loads, and told why there.
    [SkippableFact]
    public async Task AnOwnerWhoseHouseholdHasNoKey_LandsOnTheKeysPage()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var owner = $"Owner {Guid.NewGuid().ToString("N")[..4]}";
        var page = await fixture.CreatePageSignedInAsync(await OwnerSessionAsync(owner), withoutServiceWorker: true);
        await page.RouteAsync($"**{AdminProtocol.Keys}", async route =>
        {
            var answered = await route.FetchAsync();
            var keys = JsonNode.Parse(await answered.TextAsync())!.AsObject();
            keys["paysWithDeploymentKey"] = false;
            keys["openRouter"] = null;
            keys["needsOpenRouterKey"] = true;
            await route.FulfillAsync(new RouteFulfillOptions { Response = answered, Body = keys.ToJsonString() });
        });

        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);

        await page.WaitForURLAsync(new Regex("/admin/keys$"),
            new PageWaitForURLOptions { Timeout = 60_000, WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByRole(AriaRole.Note))
            .ToContainTextAsync("answer nobody until it has an OpenRouter key", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    // The stack's people are members; an owner is made for the test, as the operator's command makes one.
    private async Task<string> OwnerSessionAsync(string name)
    {
        var (_, created) = await fixture.AccountsAsync("create-user", "--name", name, "--role", "owner");
        var user = WhoWasMade().Match(created).Groups["user"].Value;
        var (_, session) = await fixture.AccountsAsync("mint-session", "--user", user);
        return session.Trim();
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household")]
    private static partial Regex WhoWasMade();
}