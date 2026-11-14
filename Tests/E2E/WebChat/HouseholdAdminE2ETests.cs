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
// Its owners are the stack's second household's (PersonElsewhereAsync), not the home's: an agent added
// or renamed has every open page of the household fetch its agents again, which in the home caught the
// other collections' model-menu taps mid-way. Only the household's name is changed here and put back
// after, and an agent added for a test is removed by it; the zone and the language are saved as they
// were. What a zone or a language does to a turn is the admin API's seam (AdminApiTests).
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

    // The household's agents are what its turns run, so this adds one of its own from a template,
    // names it, restores the name it replaced and removes it, leaving the household's agents as they
    // were — and removes it through the API should the page not get that far. What a rename does
    // to a turn is the admin API's seam (HouseholdAgentsAdminTests).
    [SkippableFact]
    public async Task AnOwner_AddsAnAgent_RenamesIt_RestoresItsName_AndRemovesIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var (added, renamed) = ($"Cocina {tag}", $"Chef {tag}");
        var page = await fixture.CreatePageSignedInAsync(await OwnerSessionAsync($"Owner {tag}"));
        try
        {
            await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
            await page.GetByLabel("Household settings").ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
            await page.WaitForURLAsync(new Regex("/admin/settings$"));
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Agents" }).ClickAsync();
            await page.WaitForURLAsync(new Regex("/admin/agents$"));

            // A template says what it makes beside the picker: a household told it has no voice agent
            // finds the one whose replies are spoken aloud by reading, not by knowing the home's names.
            await page.Locator("select[name=template]").SelectOptionAsync("nabu", new LocatorSelectOptionOptions { Timeout = 30_000 });
            await Assertions.Expect(page.Locator("#admin-agent-template-description")).ToContainTextAsync("spoken aloud");
            await page.Locator("select[name=template]").SelectOptionAsync("jonas");
            await Assertions.Expect(page.Locator("#admin-agent-template-description")).ToContainTextAsync("General-purpose personal assistant");
            await page.Locator("input[name=newAgentName]").FillAsync(added);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add agent" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
            var agent = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = added });
            await Assertions.Expect(agent).ToBeVisibleAsync();
            // Any of the household's agents may be the one that answers aloud: the page offers it. Marking
            // one would change the home's voice turns, which this collection never does (admin API tests).
            await Assertions.Expect(page.Locator($"select[name=voiceAgent] option:text-is('{added}')")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#admin-agent-voice-now")).ToContainTextAsync("voice satellites");

            await agent.Locator("input[name=agentName]").FillAsync(renamed);
            await agent.Locator("textarea[name=customInstructions]").FillAsync("Answer with a recipe.");
            await agent.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" }).ClickAsync();
            var chef = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = renamed });
            await Assertions.Expect(chef).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

            await chef.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Earlier versions" }).ClickAsync();
            var versions = chef.GetByRole(AriaRole.Region, new LocatorGetByRoleOptions { Name = $"Earlier versions of {renamed}" });
            await Assertions.Expect(versions.Locator(".admin-version")).ToHaveCountAsync(1);
            await Assertions.Expect(versions).ToContainTextAsync(added);
            // What the version is, said so it cannot read as the agent having been replaced by a person:
            // the name it went by until a change, and who made the change.
            await Assertions.Expect(versions.Locator(".admin-version")).ToContainTextAsync(new Regex(@"Until .+, when .+ changed it"));
            await versions.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Restore" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = added })).ToBeVisibleAsync();

            await page.ReloadAsync();
            agent = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = added });
            await Assertions.Expect(agent.Locator("input[name=agentName]"))
                .ToHaveValueAsync(added, new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
            await Assertions.Expect(agent.Locator("textarea[name=customInstructions]")).ToHaveValueAsync("");

            await agent.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove", Exact = true }).ClickAsync();
            // What goes with it is said before it goes: nobody's conversations with it are shown again.
            await Assertions.Expect(agent.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = $"Remove {added}" }))
                .ToContainTextAsync("conversations with it are no longer shown");
            await agent.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Remove {added}" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
            await Assertions.Expect(page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = added })).ToHaveCountAsync(0);
        }
        finally
        {
            await RemoveAgentsNamedAsync(page, tag);
        }
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

    // Whatever agent the test added and the page did not remove, removed as the page would.
    private async Task RemoveAgentsNamedAsync(IPage page, string tag)
    {
        var headers = new Dictionary<string, string>
        {
            [AdminProtocol.RequestHeader] = AdminProtocol.RequestHeaderValue, ["Accept"] = "application/json"
        };
        var listed = await page.APIRequest.GetAsync(Url(AdminProtocol.Agents), new APIRequestContextOptions { Headers = headers });
        if (!listed.Ok)
        {
            return;
        }

        var agents = System.Text.Json.JsonSerializer.Deserialize<AdminAgents>(
            await listed.TextAsync(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        foreach (var agent in agents.Agents.Where(a => a.Name.EndsWith(tag, StringComparison.Ordinal)))
        {
            await page.APIRequest.DeleteAsync(Url(AdminProtocol.Agent(agent.Id)), new APIRequestContextOptions { Headers = headers });
        }
    }

    private string Url(string path) => new Uri(new Uri(fixture.WebChatUrl), path).ToString();

    // The stack's people are members; an owner is made for the test, as the operator's command makes one.
    private async Task<string> OwnerSessionAsync(string name)
    {
        return (await fixture.PersonElsewhereAsync(name, "owner")).Session;
    }

}