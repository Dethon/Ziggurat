using System.Text.RegularExpressions;
using Domain.DTOs.Admin;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The household's admin area in the chat client (seam 2 of Tenants 4c): offered to an owner, behind the
// same session, saving the household's settings through the admin API and showing what was saved; a
// member is offered no way in and refused it anyway.
//
// Every person this stack signs in is in the home household, which every collection's turns run under,
// so only the household's name is changed here — it is in no agent's prompt — and put back after; the
// zone and the language are saved as they were. What a zone or a language does to a turn is the admin
// API's seam (AdminApiTests).
[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public partial class HouseholdAdminE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AnOwner_OpensTheAdminArea_SavesTheHouseholdsSettings_AndSeesThem()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var owner = $"Owner {tag}";
        var page = await fixture.CreatePageSignedInAsync(await OwnerSessionAsync(owner));
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await Assertions.Expect(page.Locator(".signed-in-person"))
            .ToHaveAttributeAsync("title", owner, new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });

        await page.GetByLabel("Household settings").ClickAsync();

        await page.WaitForURLAsync(new Regex("/admin/settings$"));
        var name = page.Locator("input[name=name]");
        var zone = page.Locator("input[name=timeZone]");
        await Assertions.Expect(name).Not.ToHaveValueAsync("", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        var (originalName, originalZone) = (await name.InputValueAsync(), await zone.InputValueAsync());
        originalZone.ShouldBe("Europe/Madrid");

        await name.FillAsync($"Casa {tag}");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

        await page.ReloadAsync();
        await Assertions.Expect(name).ToHaveValueAsync($"Casa {tag}", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        await Assertions.Expect(zone).ToHaveValueAsync(originalZone);

        await name.FillAsync(originalName);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
    }

    [SkippableFact]
    public async Task AMember_IsOfferedNoAdminArea_AndIsRefusedIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var user = fixture.NextUserIndex();
        var page = await fixture.CreateSignedInPageAsync(user);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await Assertions.Expect(page.Locator(".signed-in-person")).ToHaveAttributeAsync(
            "title", WebChatE2EFixture.NameOf(user), new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
        await Eventually.Settle();

        (await page.GetByLabel("Household settings").CountAsync()).ShouldBe(0);

        await page.GotoAsync(fixture.DefaultSpaceUrl + "admin/settings");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Only the household's owners manage it.");
        (await page.Locator("input[name=name]").CountAsync()).ShouldBe(0);
        var asked = await page.APIRequest.GetAsync(new Uri(new Uri(fixture.WebChatUrl), AdminProtocol.Settings).ToString(),
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    [AdminProtocol.RequestHeader] = AdminProtocol.RequestHeaderValue, ["Accept"] = "application/json"
                }
            });
        asked.Status.ShouldBe(403);
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