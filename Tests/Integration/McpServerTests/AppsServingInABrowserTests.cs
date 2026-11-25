using System.Collections.Concurrent;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// What headers cannot show: that a real browser, on the local apps name the test stacks use, takes
// an app origin as a secure context (the `__Host-` cookie and the service worker both need one),
// installs the generated worker under the app's own policy, reads the generated manifest with the
// app session, and that a removed app's worker takes itself away.
public class AppsServingInABrowserTests
{
    // Chromium takes any name under .localhost for loopback and as potentially trustworthy, so plain
    // http there is a secure context, as https is through the proxy.
    private const string LocalAppsDomain = "apps.localhost";

    [Fact]
    public async Task AnAppOrigin_IsASecureContext_ThatInstallsItsWorkerAndManifest_UnderItsPolicy()
    {
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose", name: "Glucose today"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html><head><title>Glucose</title><script type="module" src="/main.js"></script></head><body><h1 id="title">Glucose</h1></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", "document.body.dataset.ran = typeof globalThis.apps;");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var host = server.Published("glucose", id);
        var origin = $"http://{host}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await SetAppCookieAsync(page, origin, server.SessionFor(AppsServer.Her, host));
        var violations = await ViolationsAsync(page);

        await page.GotoAsync(origin + "/");
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-ran", "object");

        (await page.EvaluateAsync<bool>("window.isSecureContext")).ShouldBeTrue();
        await WorkerActiveAsync(page);
        (await page.EvaluateAsync<string>("navigator.serviceWorker.getRegistration().then(r => r.active.scriptURL + ' ' + r.scope)"))
            .ShouldBe($"{origin}/_app/sw.js {origin}/");
        await page.ReloadAsync();
        (await page.EvaluateAsync<bool>("navigator.serviceWorker.controller !== null")).ShouldBeTrue();
        var cdp = await context.NewCDPSessionAsync(page);
        var manifest = (await cdp.SendAsync("Page.getAppManifest"))!.Value;
        manifest.GetProperty("errors").GetArrayLength().ShouldBe(0, manifest.ToString());
        manifest.GetProperty("data").GetString()!.ShouldContain("Glucose today");
        violations.ShouldBeEmpty();
    }

    // An import map is inline script to the policy, and the one inline script a built app may carry:
    // the page's own maps are let through by their hashes, and a module imports through one.
    [Fact]
    public async Task APagesInlineImportMap_Resolves_UnderItsPolicy()
    {
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            "<!doctype html><html><head>\r\n<script type=\"importmap\">\r\n{ \"imports\": { \"chart\": \"/lib/chart.js\" } }\r\n</script>\r\n"
            + "<script type=\"module\" src=\"/main.js\"></script></head><body></body></html>");
        await server.CreateAsync(AppsServer.Her, "/glucose/lib/chart.js", "export const drawn = 'drawn';");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", "import { drawn } from 'chart'; document.body.dataset.chart = drawn;");
        var host = server.Preview("glucose", id);
        var origin = $"http://{host}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await SetAppCookieAsync(page, origin, server.SessionFor(AppsServer.Her, host));
        var violations = await ViolationsAsync(page);

        await page.GotoAsync(origin + "/");

        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-chart", "drawn");
        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARemovedAppsInstalledWorker_TakesItselfAway()
    {
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Glucose</h1>");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var host = server.Published("glucose", id);
        var origin = $"http://{host}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await SetAppCookieAsync(page, origin, server.SessionFor(AppsServer.Her, host));
        await page.GotoAsync(origin + "/");
        await WorkerActiveAsync(page);

        await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" }, AppsServer.TurnOf(AppsServer.Her));
        await page.ReloadAsync();

        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("This app was removed");
        await Eventually.Until(async () => await RegistrationsAsync(page) == 0, "the removed app's worker unregisters itself");
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("This app was removed");
        (await page.EvaluateAsync<int>("caches.keys().then(k => k.length)")).ShouldBe(0);
    }

    // The worker claims the page once active. A predicate is read synchronously (a Promise would count
    // as true at once), and one that never activates — refused by the policy or its scope — fails the
    // case at the wait's end rather than hanging it.
    private static Task WorkerActiveAsync(IPage page) =>
        page.WaitForFunctionAsync("navigator.serviceWorker.controller !== null",
            options: new PageWaitForFunctionOptions { PollingInterval = 100, Timeout = 15_000 });

    // The removed worker reopens the window as it goes, so a read may land on a page being replaced.
    private static async Task<int?> RegistrationsAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<int>("navigator.serviceWorker.getRegistrations().then(r => r.length)");
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Execution context was destroyed", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static Task<IBrowser> LaunchAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false"
        });

    // The cookie the app sign-in will set: host-only (a URL, never a domain, which Playwright's own
    // cookie call would make it and a `__Host-` cookie refuses), Secure, the whole origin.
    private static async Task SetAppCookieAsync(IPage page, string origin, string session)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var set = await cdp.SendAsync("Network.setCookie", new Dictionary<string, object>
        {
            ["name"] = global::McpServerApps.Serving.AppSessions.CookieName, ["value"] = session, ["url"] = origin + "/",
            ["path"] = "/", ["secure"] = true, ["httpOnly"] = true, ["sameSite"] = "Lax"
        });
        set!.Value.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    // Every content-policy refusal the page meets, from the page's own report and the console.
    private static async Task<ConcurrentQueue<string>> ViolationsAsync(IPage page)
    {
        var violations = new ConcurrentQueue<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.Ordinal))
            {
                violations.Enqueue(message.Text);
            }
        };
        await page.AddInitScriptAsync("document.addEventListener('securitypolicyviolation', e => "
                                + "console.error('Content Security Policy refused ' + e.violatedDirective + ' ' + e.blockedURI))");
        return violations;
    }
}