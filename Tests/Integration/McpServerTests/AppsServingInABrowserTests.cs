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

    // A published app reads the household through the client library its origin serves, as its viewer:
    // a query's rows and a text read's content rendered into the page, and a read outside its manifest
    // handed to the app as a structured error carrying the tools' envelope.
    [Fact]
    public async Task APublishedApp_ReadsTheHouseholdThroughTheClientLibrary_AndRendersIt()
    {
        await using var sugary = await SugaryServer.StartAsync();
        await using (var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                         .GetRequiredService<global::McpServerSugary.Store.SugaryStore>(sugary.Services).For(AppsServer.Her))
        {
            db.Meals.Add(new global::McpServerSugary.Store.Meal
            {
                Id = Guid.NewGuid(), EatenAt = DateTimeOffset.Parse("2026-10-11T12:00:00Z"), Notes = "her lentils",
                Outcome = global::Domain.DTOs.Sugary.MealOutcome.High, FirstEstimate = new global::McpServerSugary.Store.Interval(30, 40, 55),
                Items = [], CarbsLow = 30, Carbs = 40, CarbsHigh = 55
            });
            await db.SaveChangesAsync();
        }

        var households = new AgentHouseholds();
        households.Add(AppsServer.Her.TenantId, new global::Domain.DTOs.AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [sugary.Endpoint], EnabledFeatures = ["filesystem"],
            WhitelistPatterns = ["domain__filesystem__*"]
        });
        await using var agent = await AppReadsAgent.StartAsync(households, new global::Domain.DTOs.SharedServers { Sugary = sugary.Endpoint });
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain, AgentUrl = agent.Address });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html><head><script type="module" src="/main.js"></script></head><body><p id="meal"></p><p id="readme"></p><p id="refused"></p></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js",
            """
            import { query, read } from '/_app/apps.mjs';
            const meals = await query('/sugary', 'select notes from meals');
            document.getElementById('meal').textContent = meals.rows[0][0];
            const readme = await globalThis.apps.read('/sugary/README.md');
            document.getElementById('readme').textContent = readme.filePath;
            try {
              await read('/vault/a.md');
            } catch (error) {
              document.getElementById('refused').textContent =
                `${error instanceof globalThis.apps.ReadError} ${error.errorCode} ${error.path} ${error.retryable}`;
            }
            """);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var host = server.Published("glucose", id);
        var origin = $"http://{host}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await SetAppCookieAsync(page, origin, server.SessionFor(AppsServer.Her, host));
        var violations = await ViolationsAsync(page);

        await page.GotoAsync(origin + "/");

        await Assertions.Expect(page.Locator("#meal")).ToHaveTextAsync("her lentils");
        await Assertions.Expect(page.Locator("#readme")).ToHaveTextAsync("/sugary/README.md");
        await Assertions.Expect(page.Locator("#refused")).ToHaveTextAsync("true permission_denied /vault/a.md false");
        violations.ShouldBeEmpty();
    }

    // A published app that breaks in a person's browser tells the agent why, through the library alone:
    // an uncaught error (thrown fifty times by one loop, reported once), an unhandled rejection, and two
    // failed reads — one the app caught, one it did not, each reported once as the read it was. A value
    // that is not an Error is named by its type, so what the app held never leaves the page.
    [Fact]
    public async Task APublishedAppThatThrows_HasItsErrorsReportedToItsErrorsFile_OnceEach()
    {
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html><head><script type="module" src="/main.js"></script></head><body></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js",
            """
            try {
              await globalThis.apps.read('/vault/a.md');
            } catch (error) {
              document.body.dataset.caught = error.errorCode;
            }
            globalThis.apps.query('/sugary', 'select notes from meals');
            for (let i = 0; i < 50; i++) {
              setTimeout(() => { throw new TypeError('rows is undefined'); }, 0);
            }
            Promise.reject(new Error('the chart never drew'));
            Promise.reject({ rows: [['her lentils']] });
            """);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var host = server.Published("glucose", id);
        var origin = $"http://{host}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await SetAppCookieAsync(page, origin, server.SessionFor(AppsServer.Her, host));
        var violations = await ViolationsAsync(page);

        await page.GotoAsync(origin + "/today");
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-caught", "transient_dependency");

        IReadOnlyList<System.Text.Json.Nodes.JsonNode> entries = [];
        await Eventually.Until(async () => (entries = await ErrorsAsync(server)).Count >= 5, "the page's five errors are reported");
        await Eventually.Settle();
        entries = await ErrorsAsync(server);

        entries.Select(e => $"{e["kind"]} {e["read"]?["path"]}".Trim()).Order().ToList().ShouldBe(
            ["error", "read /sugary", "read /vault/a.md", "rejection", "rejection"], string.Join("\n", entries));
        entries.ShouldContain(e => e["kind"]!.GetValue<string>() == "error"
                                   && e["message"]!.GetValue<string>() == "TypeError: rows is undefined");
        entries.ShouldContain(e => e["message"]!.GetValue<string>() == "Error: the chart never drew"
                                   && e["stack"]!.GetValue<string>().Contains("main.js"));
        entries.ShouldContain(e => e["read"]!["envelope"]!["errorCode"]!.GetValue<string>() == "transient_dependency"
                                   && e["read"]!["operation"]!.GetValue<string>() == "query");
        entries.ShouldAllBe(e => e["route"]!.GetValue<string>() == "/today" && e["viewer"]!.GetValue<string>() == AppsServer.Her.UserId);
        string.Join("\n", entries).ShouldNotContain("her lentils");
        violations.ShouldBeEmpty();
    }

    private static async Task<IReadOnlyList<System.Text.Json.Nodes.JsonNode>> ErrorsAsync(AppsServer server)
    {
        var read = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = "/glucose/errors" },
            AppsServer.TurnOf(AppsServer.Her));
        return read["content"]?.GetValue<string>() is not { } content
            ? []
            :
            [
                .. content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => System.Text.Json.Nodes.JsonNode.Parse(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])!)
            ];
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

    // The app sign-in as a browser walks it: a person signed in on the main host opens their app's link
    // and lands on the page they asked for, signed in to the app, through the redirects there and back
    // and the `__Host-` cookies the app's origin sets on the way (host-only, Secure on plain http under
    // .localhost).
    [Fact]
    public async Task ASignedInPerson_OpensTheirAppsLink_AndLandsInIt()
    {
        await using var accounts = await AccountsServer.StartAsync(s => s with
        {
            AppsDomain = LocalAppsDomain,
            Signing = new global::Domain.Security.Signing.SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
            Callers = new Dictionary<string, string> { ["mcp-apps"] = McpTestSecret.Gate.SharedSecret }
        });
        var fran = await accounts.MintOperatorAsync("Fran");
        var author = new global::Domain.DTOs.Channel.Account(fran.TenantId, fran.UserId);
        await using var server = await AppsServer.StartAsync(
            s => s with
            {
                AppsDomain = LocalAppsDomain,
                PublicBaseUrl = accounts.Address.ToString().TrimEnd('/'),
                Accounts = s.Accounts with { BaseUrl = accounts.Address.ToString() }
            },
            services => Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(services,
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<TimeProvider>(accounts.Clock)));
        var id = AppsServer.IdIn(await server.AppAsync(author, "glucose"));
        await server.CreateAsync(author, "/glucose/index.html", "<!doctype html><title>Glucose</title><h1>Glucose</h1>");
        await server.ExecAsync(author, "/glucose", "./publish");
        var origin = $"http://{server.Published("glucose", id)}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var context = await browser.NewContextAsync();
        await context.AddCookiesAsync([
            new Cookie
            {
                Name = global::Accounts.Sessions.SessionCookie.Name, Value = await accounts.MintSessionAsync(fran.UserId),
                Url = accounts.Address.ToString(), HttpOnly = true, Secure = true, SameSite = SameSiteAttribute.Lax
            }
        ]);
        var page = await context.NewPageAsync();

        await page.GotoAsync(origin + "/week");

        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Glucose");
        page.Url.ShouldBe(origin + "/week");
        (await context.CookiesAsync([origin])).ShouldContain(c => c.Name == global::McpServerApps.Serving.AppSessions.CookieName);
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