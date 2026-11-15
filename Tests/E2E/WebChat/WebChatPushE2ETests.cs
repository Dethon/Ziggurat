using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The installed app's own worker handling a real push (DevTools delivers it as a push service would):
// a finished reply's notification shows what it said and leads to its conversation, one notification
// per conversation. The tap itself cannot be made here; it navigates to the notification's url, which
// AnAddressNamingAConversation_OpensIt covers.
[Collection(WebChatE2ECollections.Topics)]
[Trait("Category", "E2E")]
public class WebChatPushE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AFinishedRepliesPush_ShowsWhatItSaid_AndLeadsToItsConversation()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        // The suite's shared browser is Chromium's headless shell, which answers every notification
        // permission "denied"; the full build in its new headless mode shows them. The person's
        // session is carried over from a page the fixture signed in.
        var signedIn = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        var cookies = await signedIn.Context.CookiesAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "chromium",
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false"
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(cookies.Select(c => new Cookie
        {
            Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Expires = c.Expires,
            HttpOnly = c.HttpOnly, Secure = c.Secure, SameSite = c.SameSite
        }));
        await context.GrantPermissionsAsync(["notifications"]);
        var page = await context.NewPageAsync();
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        var origin = new Uri(page.Url).GetLeftPart(UriPartial.Authority);
        await page.EvaluateAsync("async () => { await navigator.serviceWorker.ready; }");

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var registration = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        cdp.Event("ServiceWorker.workerRegistrationUpdated").OnEvent += (_, update) =>
        {
            var live = update?.GetProperty("registrations").EnumerateArray()
                .FirstOrDefault(r => r.GetProperty("scopeURL").GetString()!.StartsWith(origin)
                                     && !r.GetProperty("isDeleted").GetBoolean());
            if (live is { ValueKind: JsonValueKind.Object } found)
            {
                registration.TrySetResult(found.GetProperty("registrationId").GetString()!);
            }
        };
        await cdp.SendAsync("ServiceWorker.enable");
        var registrationId = await registration.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Addressed to a space this page is not on, or the worker stays quiet as it should.
        foreach (var body in new[] { "Two flats fit", "Three flats fit your budget" })
        {
            await cdp.SendAsync("ServiceWorker.deliverPushMessage", new Dictionary<string, object>
            {
                ["origin"] = origin,
                ["registrationId"] = registrationId,
                ["data"] = JsonSerializer.Serialize(new
                {
                    title = "Apartment search",
                    body,
                    url = "/elsewhere",
                    spaceSlug = "default",
                    agentId = "jonas",
                    topicId = "topic 7"
                })
            });
        }

        // Asked until the second has replaced the first: the worker shows them as each push arrives.
        string? shown = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (shown is null && DateTime.UtcNow < deadline)
        {
            shown = await page.EvaluateAsync<string?>(
                """
                async () => {
                    const registration = await navigator.serviceWorker.ready;
                    const shown = await registration.getNotifications();
                    return shown.length > 0 && shown.every(n => n.body !== 'Two flats fit')
                        ? JSON.stringify(shown.map(n => ({ title: n.title, body: n.body, tag: n.tag, url: n.data?.url })))
                        : null;
                }
                """);
            if (shown is null)
            {
                await page.WaitForTimeoutAsync(200);
            }
        }

        shown.ShouldNotBeNull("the worker showed no notification for the second push");
        using var notifications = JsonDocument.Parse(shown);
        var only = notifications.RootElement.EnumerateArray().ShouldHaveSingleItem();
        only.GetProperty("title").GetString().ShouldBe("Apartment search");
        only.GetProperty("body").GetString().ShouldBe("Three flats fit your budget");
        only.GetProperty("tag").GetString().ShouldBe("topic:topic 7");
        only.GetProperty("url").GetString().ShouldBe("/elsewhere?agent=jonas&topic=topic%207");
    }
}