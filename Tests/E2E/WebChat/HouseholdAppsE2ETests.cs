using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The household's limits on its apps' asks in the admin area (seam 2): an owner sets the household's limit
// and finds it saved after a reload, in the stack's second household (a limit of the home's is read by every
// app turn of it), put back as found; and sets a published app's own limit and removes one kept for an app no
// longer listed, against an answer routed in place of the admin API's, since the stack's households publish
// no apps. What a limit does to an ask is the origin's seam (AppAskCapsOverTheWireTests); the merge, bounds
// and apps of the household the admin API's (HouseholdAppAsksAdminTests).
[Collection(WebChatE2ECollections.AdminPages)]
[Trait("Category", "E2E")]
public class HouseholdAppsE2ETests(WebChatE2EFixture fixture)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private const string HouseholdLimit = "Asks per day, each app, whole household";

    [SkippableFact]
    public async Task AnOwner_SetsTheHouseholdsLimit_AndFindsItSaved()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var (_, session) = await fixture.PersonElsewhereAsync($"Owner {Guid.NewGuid().ToString("N")[..6]}", "owner");
        var page = await fixture.CreatePageSignedInAsync(session);
        // A value of this run's, so an earlier run's save is never what the reload finds.
        var limit = Random.Shared.Next(1, 9_999).ToString();
        try
        {
            await page.GotoAsync(fixture.DefaultSpaceUrl + "admin/apps");
            var field = page.GetByRole(AriaRole.Spinbutton, new PageGetByRoleOptions { Name = HouseholdLimit });
            await Assertions.Expect(field).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
            await field.FillAsync(limit);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save limits" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");

            await page.ReloadAsync();

            await Assertions.Expect(field).ToHaveValueAsync(limit, new LocatorAssertionsToHaveValueOptions { Timeout = 60_000 });
        }
        finally
        {
            await PutBackAsync(page);
        }
    }

    [SkippableFact]
    public async Task AnOwner_SetsAnAppsOwnLimit_AndRemovesOneKeptForAnAppNoLongerListed()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var (_, session) = await fixture.PersonElsewhereAsync($"Owner {Guid.NewGuid().ToString("N")[..6]}", "owner");
        // No service worker: a request it makes for the page is one page.RouteAsync never sees.
        var page = await fixture.CreatePageSignedInAsync(session, withoutServiceWorker: true);
        var loaded = new AppAsksSection { DailyCap = 50, Apps = new Dictionary<string, int> { ["d4e5f6"] = 5, ["g7h8i9"] = 2 } };
        var answer = new AdminAppAsks(loaded,
        [
            new AdminCappedApp("a1b2c3", "Glucose today", "#e9601f", "Ana", "https://glucose-a1b2c3.apps.test/"),
            new AdminCappedApp("d4e5f6", "Carbs", "#2a9d8f", "", "https://carbs-d4e5f6.apps.test/")
        ]);
        var sent = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(url => new Uri(url).AbsolutePath == AdminProtocol.AppAsks, async route =>
        {
            if (route.Request.Method == "PUT")
            {
                sent.TrySetResult(JsonNode.Parse(route.Request.PostData!)!);
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = route.Request.Method == "PUT"
                    ? JsonSerializer.Serialize(new AdminSaved<AdminAppAsks>(answer, null), _json)
                    : JsonSerializer.Serialize(answer, _json)
            });
        });

        await page.GotoAsync(fixture.DefaultSpaceUrl + "admin/apps");
        var glucose = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = "Glucose today" });
        await Assertions.Expect(glucose).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        var carbs = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = "Carbs" });
        var gone = page.GetByRole(AriaRole.Article, new PageGetByRoleOptions { Name = "g7h8i9" });
        await Assertions.Expect(page.GetByRole(AriaRole.Spinbutton, new PageGetByRoleOptions { Name = HouseholdLimit })).ToHaveValueAsync("50");
        await Assertions.Expect(glucose).ToContainTextAsync("Made by Ana");
        await Assertions.Expect(carbs).ToContainTextAsync("Made by a former member");
        await Assertions.Expect(carbs.GetByRole(AriaRole.Spinbutton)).ToHaveValueAsync("5");
        await Assertions.Expect(gone.GetByRole(AriaRole.Spinbutton)).ToBeDisabledAsync();
        await Assertions.Expect(gone.GetByRole(AriaRole.Spinbutton)).ToHaveValueAsync("2");

        await glucose.GetByRole(AriaRole.Spinbutton).FillAsync("0");
        await gone.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove its limit" }).ClickAsync();
        await Assertions.Expect(gone.GetByRole(AriaRole.Spinbutton)).ToHaveValueAsync("");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save limits" }).ClickAsync();

        var body = await sent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Caps(body).ShouldBe([("a1b2c3", 0), ("d4e5f6", 5)]);
        ((int)body["dailyCap"]!).ShouldBe(50);
        Caps(body[AdminEdit.Loaded]!).ShouldBe([("d4e5f6", 5), ("g7h8i9", 2)]);
    }

    private static IReadOnlyList<(string, int)> Caps(JsonNode section) =>
        [.. section["apps"]!.AsObject().Select(cap => (cap.Key, (int)cap.Value!)).OrderBy(cap => cap.Key)];

    // The second household's limit back to none, through the API the page calls, whatever the page got to.
    private async Task PutBackAsync(IPage page)
    {
        var headers = new Dictionary<string, string>
        {
            [AdminProtocol.RequestHeader] = AdminProtocol.RequestHeaderValue, ["Accept"] = "application/json"
        };
        var read = await page.APIRequest.GetAsync(Url(AdminProtocol.AppAsks), new APIRequestContextOptions { Headers = headers });
        if (!read.Ok)
        {
            return;
        }

        var caps = JsonSerializer.Deserialize<AdminAppAsks>(await read.TextAsync(), _json)!.Caps;
        await page.APIRequest.PutAsync(Url(AdminProtocol.AppAsks), new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string>(headers) { ["Content-Type"] = "application/json" },
            Data = JsonSerializer.Serialize(caps with { DailyCap = null }, _json)
        });
    }

    private string Url(string path) => new Uri(new Uri(fixture.WebChatUrl), path).ToString();
}