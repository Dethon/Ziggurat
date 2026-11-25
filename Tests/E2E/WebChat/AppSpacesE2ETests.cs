using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Microsoft.Playwright;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Integration.Fixtures;

namespace Tests.E2E.WebChat;

// An app space from WebChat's space switcher to the app's own origin (the spec's fourth seam): a member
// of the household that shares it finds it among the household's spaces, opens it, is signed in to its
// origin by the app sign-in, and the app shows what it read through /_vfs/*; a member of another household
// opening the same link gets the one 404.
//
// The app is built and published as its building agent would, over the apps server's /mcp on a call
// token signed with the agent's key, and the note it reads is written to the vault the same way: no model
// turn. It lives in the stack's second household, whose agents include one whose vault reads run unasked
// (WebChatStack.AppReaderAgent) and whose apps feature is on, so nothing of the home, which every other
// collection reads, changes; the stranger is a person of the home.
[Collection(WebChatE2ECollections.AppSpaces)]
[Trait("Category", "E2E")]
public class AppSpacesE2ETests(AppSpacesE2EFixture fixture)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task AMember_OpensAnAppSpaceFromTheSwitcher_SignedInToItsOrigin_AndAnotherHouseholdsMemberGetsA404()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var slug = $"pantry-{tag}";
        var name = $"Pantry {tag}";
        var (ownerId, ownerSession) = await fixture.PersonElsewhereAsync($"Owner {tag}", "owner");
        var (_, memberSession) = await fixture.PersonElsewhereAsync($"Member {tag}", "member");
        var owner = new Account(fixture.ElsewhereHousehold, ownerId);

        await CallAsync(fixture.VaultMcpEndpoint, "mcp-vault", owner, "fs_create", new()
        {
            ["path"] = $"/vault/pantry-{tag}.md", ["content"] = $"Lemons {tag}: three", ["overwrite"] = true
        });
        var link = await PublishedAppAsync(owner, slug, name, tag);
        await ShareAsync(ownerSession, slug, name, link);
        try
        {
            var page = await fixture.CreatePageSignedInAsync(memberSession);
            await WebChatE2ETests.GotoWebChatAsync(page, fixture.DefaultSpaceUrl);
            await WebChatE2ETests.SelectAgentAsync(page);

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Spaces", Exact = true }).ClickAsync();
            var app = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = name });
            await Assertions.Expect(app).ToHaveAttributeAsync("href", link);
            await app.ClickAsync();

            await page.WaitForURLAsync(new Regex($"^{Regex.Escape(link)}"), new PageWaitForURLOptions { Timeout = 60_000 });
            await Assertions.Expect(page.Locator("#out")).ToContainTextAsync($"Lemons {tag}: three",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

            var stranger = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
            var opened = await stranger.GotoAsync(link);
            opened.ShouldNotBeNull().Status.ShouldBe((int)HttpStatusCode.NotFound);
            new Uri(stranger.Url).Host.ShouldBe(new Uri(link).Host, "signed in through the app sign-in, then refused");
        }
        finally
        {
            await UnshareAsync(ownerSession, slug);
        }
    }

    // The app as its building agent writes it: a manifest reading the vault, a page whose own script reads
    // the note through the client library and shows it (inline script never runs), then ./publish. Its link.
    private async Task<string> PublishedAppAsync(Account author, string slug, string name, string tag)
    {
        await CallAsync(fixture.AppsMcpEndpoint, "mcp-apps", author, "fs_create", new()
        {
            ["path"] = $"/{slug}/app.json",
            ["content"] = $$"""{"name":"{{name}}","accentColor":"#2a9d8f","description":"What the pantry holds.","reads":["/vault"]}""",
            ["createDirectories"] = true
        });
        await CallAsync(fixture.AppsMcpEndpoint, "mcp-apps", author, "fs_create", new()
        {
            ["path"] = $"/{slug}/index.html",
            ["content"] = """<!doctype html><html><head><title>Pantry</title><script src="app.js" defer></script></head><body><main id="out">Reading…</main></body></html>"""
        });
        await CallAsync(fixture.AppsMcpEndpoint, "mcp-apps", author, "fs_create", new()
        {
            ["path"] = $"/{slug}/app.js",
            ["content"] = $$"""
                apps.read('/vault/pantry-{{tag}}.md')
                    .then(read => { document.getElementById('out').textContent = read.content; })
                    .catch(error => { document.getElementById('out').textContent = 'refused: ' + (error.errorCode || error.message); });
                """
        });
        var published = await CallAsync(fixture.AppsMcpEndpoint, "mcp-apps", author, "fs_exec", new()
        {
            ["path"] = $"/{slug}", ["command"] = "./publish"
        });
        return Regex.Match(published.ToJsonString(), $@"https://{Regex.Escape(slug)}-[a-z0-9]{{6}}\.{Regex.Escape(WebChatStack.AppsDomain)}/")
                   is { Success: true } found
            ? found.Value
            : throw new ShouldAssertException($"./publish named no link: {published.ToJsonString()}");
    }

    // The owner shares it from the spaces page's API, approving the reach it asks, as the page would.
    private async Task ShareAsync(string ownerSession, string slug, string name, string link)
    {
        using var admin = Admin(ownerSession);
        var loaded = (await admin.GetFromJsonAsync<AdminSpaces>(AdminProtocol.Spaces.TrimStart('/'), _json))!;
        var app = loaded.Apps.ShouldNotBeNull().Single(a => a.Url == link);
        var shared = await admin.PutAsJsonAsync(AdminProtocol.Spaces.TrimStart('/'), AdminEdit.Of(
            new SpacesSection
            {
                Spaces =
                [
                    .. loaded.Spaces,
                    new HouseholdSpace
                    {
                        Slug = slug, Name = name, AppId = app.Id, Order = loaded.Spaces.Max(s => s.Order) + 1, Approved = app.Reach
                    }
                ]
            },
            new SpacesSection { Spaces = loaded.Spaces }), _json);
        shared.StatusCode.ShouldBe(HttpStatusCode.OK, await shared.Content.ReadAsStringAsync());
    }

    private async Task UnshareAsync(string ownerSession, string slug)
    {
        using var admin = Admin(ownerSession);
        var loaded = (await admin.GetFromJsonAsync<AdminSpaces>(AdminProtocol.Spaces.TrimStart('/'), _json))!;
        if (loaded.Spaces.Any(s => s.Slug == slug))
        {
            await admin.PutAsJsonAsync(AdminProtocol.Spaces.TrimStart('/'),
                new SpacesSection { Spaces = [.. loaded.Spaces.Where(s => s.Slug != slug)] }, _json);
        }
    }

    // The admin API through the stack's proxy, as the admin area calls it.
    private HttpClient Admin(string session)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(fixture.DefaultSpaceUrl) };
        client.DefaultRequestHeaders.Add("Cookie", $"{WebChatE2EFixture.SessionCookieName}={session}");
        client.DefaultRequestHeaders.Add(AdminProtocol.RequestHeader, AdminProtocol.RequestHeaderValue);
        return client;
    }

    // A tool called as the agent calls it on a turn of the app reader's, for this person.
    private static async Task<JsonNode> CallAsync(
        string endpoint, string audience, Account person, string tool, Dictionary<string, object?> arguments)
    {
        await using var mcp = await McpClient.CreateAsync(McpTestSecret.Transport(endpoint, McpTestSecret.For(audience), audience));
        var turn = new ConversationContext(WebChatStack.AppReaderAgent, "e2e-app-spaces", person.UserId,
            new ReplyTarget("signalr", "e2e-app-spaces"), Principal: new Principal("signalr", person.UserId, person));
        var result = await mcp.CallToolAsync(new CallToolRequestParams
        {
            Name = tool,
            Arguments = arguments.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value)),
            Meta = new JsonObject
            {
                [ChannelProtocol.ConversationContextMetaKey] = JsonSerializer.SerializeToNode(turn, ChannelProtocol.SerializerOptions)
            }
        });
        var answer = JsonNode.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))!;
        (answer["ok"]?.GetValue<bool>() ?? true).ShouldBeTrue($"{tool} on {audience}: {answer.ToJsonString()}");
        return answer;
    }
}