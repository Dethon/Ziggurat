using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The household's spaces and features in the admin area (seam 2 of Tenants 4c): an owner adds a space
// with its colour and the agent it opens on, finds it in the chat client as saved, edits it and finds
// the edit there, and removes it.
//
// Every person this stack signs in is in the home household, which every collection runs under: the
// space this test adds is its own, tagged, and removed by it — through the API should the page not get
// that far — and the home's spaces are saved as the page loaded them. A feature is read by every turn,
// so the features page is opened and never saved here; what a toggle does to a turn is the admin API's
// seam (HouseholdFeaturesAdminTests), and where a removed space's topics are listed is the hub's
// (ChatHubSpacesTests).
[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public partial class HouseholdSpacesE2ETests(WebChatE2EFixture fixture)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task AnOwner_AddsASpace_FindsItInTheChat_EditsIt_AndRemovesIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..6];
        var (slug, added, renamed) = ($"e2e-{tag}", $"Attic {tag}", $"Cellar {tag}");
        var page = await fixture.CreatePageSignedInAsync(await OwnerSessionAsync($"Owner {tag}"));
        try
        {
            await OpenSpacesAsync(page);
            await page.Locator("input[name=newSpaceSlug]").FillAsync(slug);
            await page.Locator("input[name=newSpaceName]").FillAsync(added);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add space" }).ClickAsync();
            var space = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = slug, Exact = true });
            await space.Locator("input[name=accentColor]").FillAsync("#2a9d8f");
            await space.Locator("select[name=defaultAgent]").SelectOptionAsync("vision-agent");
            await SaveAsync(page);
            await Assertions.Expect(page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = added })).ToBeVisibleAsync();

            await ExpectSpaceAsync(page, slug, added, "#2a9d8f", "Vision Agent");

            await OpenSpacesAsync(page);
            space = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = added });
            await space.Locator("input[name=spaceName]").FillAsync(renamed);
            await space.Locator("input[name=accentColor]").FillAsync("#8338ec");
            await space.Locator("select[name=defaultAgent]").SelectOptionAsync("test-agent");
            await SaveAsync(page);

            await ExpectSpaceAsync(page, slug, renamed, "#8338ec", "Test Agent");

            await OpenSpacesAsync(page);
            space = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = renamed });
            await space.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove" }).ClickAsync();
            await Assertions.Expect(space).ToHaveCountAsync(0);
            await SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Article).First).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = renamed })).ToHaveCountAsync(0);
        }
        finally
        {
            await RemoveSpacesTaggedAsync(page, slug);
        }
    }

    [SkippableFact]
    public async Task AnOwner_IsShownTheHouseholdsFeatures()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageSignedInAsync(await OwnerSessionAsync($"Owner {Guid.NewGuid().ToString("N")[..6]}"));
        await page.GotoAsync(fixture.DefaultSpaceUrl + "admin/features");

        var toggles = page.GetByRole(AriaRole.Checkbox);
        await Assertions.Expect(toggles).ToHaveCountAsync(6, new LocatorAssertionsToHaveCountOptions { Timeout = 60_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions { Name = "Memory" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save" })).ToBeEnabledAsync();
    }

    private async Task OpenSpacesAsync(IPage page)
    {
        await page.GotoAsync(fixture.DefaultSpaceUrl + "admin/spaces");
        await Assertions.Expect(page.GetByRole(AriaRole.Article).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
    }

    private static async Task SaveAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save spaces" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
    }

    // What a person sees of the space in the chat: its name in the title, its colour, and the agent it
    // opens on for someone who never picked one there.
    private async Task ExpectSpaceAsync(IPage page, string slug, string name, string colour, string agent)
    {
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.DefaultSpaceUrl + slug);
        await Assertions.Expect(page.Locator(".agent-seg.on:visible").First)
            .ToContainTextAsync(agent, new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(page).ToHaveTitleAsync(new Regex($"{Regex.Escape(name)}$"));
        await page.WaitForFunctionAsync(
            "color => getComputedStyle(document.documentElement).getPropertyValue('--space-accent').trim() === color",
            colour);
    }

    // The test's own space, removed as the page would remove it, whatever state the page got to.
    private async Task RemoveSpacesTaggedAsync(IPage page, string slug)
    {
        var headers = new Dictionary<string, string>
        {
            [AdminProtocol.RequestHeader] = AdminProtocol.RequestHeaderValue, ["Accept"] = "application/json"
        };
        var listed = await page.APIRequest.GetAsync(Url(AdminProtocol.Spaces), new APIRequestContextOptions { Headers = headers });
        if (!listed.Ok)
        {
            return;
        }

        var spaces = JsonSerializer.Deserialize<AdminSpaces>(await listed.TextAsync(), _json)!;
        if (spaces.Spaces.All(s => s.Slug != slug))
        {
            return;
        }

        await page.APIRequest.PutAsync(Url(AdminProtocol.Spaces), new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string>(headers) { ["Content-Type"] = "application/json" },
            Data = JsonSerializer.Serialize(new SpacesSection { Spaces = [.. spaces.Spaces.Where(s => s.Slug != slug)] }, _json)
        });
    }

    private string Url(string path) => new Uri(new Uri(fixture.WebChatUrl), path).ToString();

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