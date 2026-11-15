using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The installed app's own worker handling a real push (DevTools delivers it as a push service would):
// a finished reply's notification shows what it said and leads to its conversation, one notification
// per conversation, and none on a page already showing the space. The tap itself cannot be made here;
// it navigates to the notification's url, which AnAddressNamingAConversation_OpensIt covers.
[Collection(WebChatE2ECollections.Topics)]
[Trait("Category", "E2E")]
public class WebChatPushE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task AFinishedRepliesPush_ShowsWhatItSaid_AndLeadsToItsConversation()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        await using var app = await PushedApp.OpenAsync(fixture);

        // Addressed to a space this page is not on, or the worker stays quiet as it should.
        await app.DeliverAsync("Two flats fit", "/elsewhere");
        await app.DeliverAsync("Three flats fit your budget", "/elsewhere");

        var only = (await app.ShownOnceAsync(shown => shown.All(n => n.GetProperty("body").GetString() != "Two flats fit")))
            .ShouldHaveSingleItem();
        only.GetProperty("title").GetString().ShouldBe("Apartment search");
        only.GetProperty("body").GetString().ShouldBe("Three flats fit your budget");
        only.GetProperty("tag").GetString().ShouldBe("topic:topic 7");
        only.GetProperty("url").GetString().ShouldBe("/elsewhere?agent=jonas&topic=topic%207");
    }

    // A page showing the space shows the reply or the approval card already, so the worker stays
    // quiet there however the address is spelled (a trailing slash; the front page for /default).
    [SkippableFact]
    public async Task APushForTheSpaceOnScreen_ShowsNothing()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        await using var app = await PushedApp.OpenAsync(fixture);

        await app.DeliverAsync("Quiet here", new Uri(app.Page.Url).AbsolutePath + "/");
        await app.DeliverAsync("Shown elsewhere", "/elsewhere");

        (await app.ShownOnceAsync(shown => shown.Count > 0))
            .ShouldHaveSingleItem().GetProperty("body").GetString().ShouldBe("Shown elsewhere");
    }

    private sealed class PushedApp : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly ICDPSession _cdp;
        private readonly string _origin;
        private readonly string _registrationId;

        private PushedApp(IPlaywright playwright, IBrowser browser, IPage page, ICDPSession cdp,
            string origin, string registrationId)
        {
            _playwright = playwright;
            _browser = browser;
            Page = page;
            _cdp = cdp;
            _origin = origin;
            _registrationId = registrationId;
        }

        public IPage Page { get; }

        // The suite's shared browser is Chromium's headless shell, which answers every notification
        // permission "denied"; the full build in its new headless mode shows them. The person's
        // session is carried over from a page the fixture signed in.
        public static async Task<PushedApp> OpenAsync(WebChatE2EFixture fixture)
        {
            var signedIn = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
            var cookies = await signedIn.Context.CookiesAsync();
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "chromium",
                Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false"
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            await context.AddCookiesAsync(cookies.Select(c => new Cookie
            {
                Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Expires = c.Expires,
                HttpOnly = c.HttpOnly, Secure = c.Secure, SameSite = c.SameSite
            }));
            await context.GrantPermissionsAsync(["notifications"]);
            var page = await context.NewPageAsync();
            await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);

            // Settled on its space's page: the first load moves a removed or unnamed space to the
            // household's first, and the quiet check reads the page's address.
            await Assertions.Expect(page.Locator("textarea.chat-input"))
                .ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 60_000 });
            var origin = new Uri(page.Url).GetLeftPart(UriPartial.Authority);
            await page.EvaluateAsync("async () => { await navigator.serviceWorker.ready; }");

            var cdp = await context.NewCDPSessionAsync(page);
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

            return new PushedApp(playwright, browser, page, cdp, origin, registrationId);
        }

        public Task DeliverAsync(string body, string url) =>
            _cdp.SendAsync("ServiceWorker.deliverPushMessage", new Dictionary<string, object>
            {
                ["origin"] = _origin,
                ["registrationId"] = _registrationId,
                ["data"] = JsonSerializer.Serialize(new
                {
                    title = "Apartment search",
                    body,
                    url,
                    spaceSlug = "default",
                    agentId = "jonas",
                    topicId = "topic 7"
                })
            });

        // Asked until the notifications shown satisfy the test: the worker shows them as each push
        // arrives, in the order they were delivered.
        public async Task<IReadOnlyList<JsonElement>> ShownOnceAsync(Func<IReadOnlyList<JsonElement>, bool> settled)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var json = await Page.EvaluateAsync<string>(
                    """
                    async () => {
                        const registration = await navigator.serviceWorker.ready;
                        const shown = await registration.getNotifications();
                        return JSON.stringify(shown.map(n => ({ title: n.title, body: n.body, tag: n.tag, url: n.data?.url })));
                    }
                    """);
                var shown = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
                if (shown.Count > 0 && settled(shown))
                {
                    return shown;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"the worker's notifications never settled: {json}");
                }

                await Page.WaitForTimeoutAsync(200);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }
    }
}