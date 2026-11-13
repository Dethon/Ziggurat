using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// Signing in on the chat client's own pages: a signed-out browser that comes back as itself, and an
// enrollment link a worker from before answered. Each makes its own person.
[Collection(WebChatE2ECollections.SignIn)]
[Trait("Category", "E2E")]
public class WebChatSignInE2ETests(WebChatE2EFixture fixture)
{
    // A browser that installed the chat client before sign-in existed holds a worker that answers
    // every navigation with its own cached app, so the passkey link the upgrade's migration prints
    // opened there showed the old app's "nothing at this address". That navigation is also what
    // fetches and activates the new worker, which then sends such a page on to the accounts page it
    // asked for, rather than leaving the person to guess that a reload would.
    [SkippableFact]
    public async Task AnEnrollmentLinkAnOldWorkerAnswered_ReachesEnrollmentOnceTheNewWorkerTakesOver()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var (_, created) = await fixture.AccountsAsync("create-user", "--name", $"Old-{Guid.NewGuid().ToString("N")[..4]}");
        var userId = Regex.Match(created, "user (?<id>[0-9a-f]+),").Groups["id"].Value;
        var (_, minted) = await fixture.AccountsAsync("mint-link", "--user", userId);
        var link = minted.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("http", StringComparison.Ordinal));

        // The worker a master deployment shipped, as far as this needs it: every navigation is its app.
        // Installed on a profile of its own, by a browser that routes its script to it.
        const string oldWorker = """
                                 self.addEventListener('install', () => self.skipWaiting());
                                 self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));
                                 self.addEventListener('fetch', e => {
                                   if (e.request.mode === 'navigate')
                                     e.respondWith(new Response('<html><body>the old app</body></html>', { headers: { 'content-type': 'text/html' } }));
                                 });
                                 """;
        var profile = Directory.CreateTempSubdirectory("old-worker-").FullName;
        try
        {
            await using (var before = await fixture.OpenProfileAsync(profile))
            {
                await before.RouteAsync("**/service-worker.js", route => route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200, ContentType = "application/javascript", Body = oldWorker
                }));
                var installing = before.Pages.Count > 0 ? before.Pages[0] : await before.NewPageAsync();
                await installing.GotoAsync(link);
                await installing.EvaluateAsync(
                    "async () => { await navigator.serviceWorker.register('/service-worker.js'); await navigator.serviceWorker.ready; }");
                await installing.GotoAsync(link);
                await Assertions.Expect(installing.GetByText("the old app")).ToBeVisibleAsync();
            }

            // The deploy: the same browser opened again, and the worker's script is the new one.
            await using var after = await fixture.OpenProfileAsync(profile);
            var page = after.Pages.Count > 0 ? after.Pages[0] : await after.NewPageAsync();
            await page.GotoAsync(link, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });

            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create a passkey" }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    // The proxy sends a page with no session to sign in, and sign-in brings it back to where it was
    // going, as the account whose passkey answered. A real ceremony, against Chromium's virtual
    // authenticator, through the stack's own Caddy and accounts service.
    [SkippableFact]
    public async Task ASignedOutBrowser_SignsInAndComesBackAsThemselves()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var name = $"Passkey-{Guid.NewGuid().ToString("N")[..4]}";
        var (_, created) = await fixture.AccountsAsync("create-user", "--name", name);
        var userId = Regex.Match(created, "user (?<id>[0-9a-f]+),").Groups["id"].Value;
        var (_, minted) = await fixture.AccountsAsync("mint-link", "--user", userId);
        var link = minted.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("http", StringComparison.Ordinal));

        var page = await fixture.CreatePageAsync();
        await AddVirtualAuthenticatorAsync(page);

        // Enrolling lands the browser on the deployment's front page, signed in.
        await page.GotoAsync(link);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create a passkey" }).ClickAsync();
        await page.WaitForURLAsync(fixture.DefaultSpaceUrl);

        // The front page installs the app's service worker, which answers navigations from its
        // cache: it is what an installed app is, and it must still let sign-in through.
        await page.WaitForFunctionAsync(
            "() => !!navigator.serviceWorker.controller", null, new PageWaitForFunctionOptions { Timeout = 60_000 });
        await page.Context.ClearCookiesAsync();

        await page.GotoAsync(fixture.WebChatUrl);
        await page.WaitForURLAsync(url => url.Contains("/accounts/sign-in", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign in with a passkey" }).ClickAsync();
        await page.WaitForURLAsync(fixture.WebChatUrl);

        await Assertions.Expect(page.Locator(".signed-in-person"))
            .ToHaveAttributeAsync("title", name, new LocatorAssertionsToHaveAttributeOptions { Timeout = 60_000 });
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 60_000 });
    }

    // A platform authenticator that keeps discoverable credentials and verifies its user, standing
    // in for the phone's; it belongs to the page, so enrolling and signing in share one.
    private static async Task AddVirtualAuthenticatorAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable");
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new
            {
                protocol = "ctap2",
                transport = "internal",
                hasResidentKey = true,
                hasUserVerification = true,
                isUserVerified = true
            }
        });
    }
}