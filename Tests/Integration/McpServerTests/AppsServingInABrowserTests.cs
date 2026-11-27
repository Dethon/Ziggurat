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

    // A published app asks its assistant through the client library, from the module and the classic
    // script alike: the answer's text and conversation, a follow-up on it with a schema and an image from the
    // page answered with the reply's JSON, a refusal handed to the app as an AskError carrying the tools'
    // envelope, and, with the household's cap set at the three asks before it, a fourth refused on the cap,
    // its envelope saying when the app may ask again (that nothing is sent is AppsAsksTests').
    [Fact]
    public async Task APublishedApp_AsksItsAssistantThroughTheClientLibrary_AndShowsTheAnswer()
    {
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var agent = await server.AgentAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","instructions":"asks.md"}""",
            overwrite: true);
        await server.CreateAsync(AppsServer.Her, "/glucose/asks.md", "Answer in one word.");
        server.AskCaps.Set(AppsServer.Her.TenantId, new global::Domain.DTOs.Household.AppAsksSection { DailyCap = 3 });
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html><head><script type="module" src="/main.js"></script></head><body><p id="answer"></p><p id="value"></p><p id="refused"></p><p id="capped"></p></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js",
            """
            import { ask, AskError } from '/_app/apps.mjs';
            const answer = await ask('How was my morning?');
            document.getElementById('answer').textContent = `${answer.text} ${answer.conversation.length > 0}`;
            const plate = new File([new Uint8Array([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2])], 'plate.png', { type: 'image/png' });
            const mood = await ask('And the mood?', { conversation: answer.conversation, files: [plate], schema: { name: 'mood', schema: {
              type: 'object', properties: { mood: { type: 'string' } }, required: ['mood'], additionalProperties: false } } });
            document.getElementById('value').textContent = `${mood.value.mood} ${'text' in mood}`;
            try {
              await globalThis.apps.ask('And now?');
            } catch (error) {
              document.getElementById('refused').textContent =
                `${error instanceof AskError} ${error instanceof globalThis.apps.AskError} ${error.name} ${error.errorCode} ${error.retryable}`;
            }
            try {
              await ask('Once more?');
            } catch (error) {
              document.getElementById('capped').textContent =
                `${error instanceof AskError} ${error.errorCode} ${error.retryable} ${!Number.isNaN(Date.parse(error.envelope.freesAt))}`;
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
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = await agent.Messages.FirstAsync(wait.Token);
        first.Content.ShouldBe("How was my morning?");
        await ReplyAsync(agent, first, global::Domain.DTOs.ReplyContentType.Text, "Quietly.");
        await ReplyAsync(agent, first, global::Domain.DTOs.ReplyContentType.StreamComplete, "");
        var structured = await agent.Messages.FirstAsync(wait.Token);
        structured.ResponseFormat.ShouldNotBeNull().Name.ShouldBe("mood");
        structured.ConversationId.ShouldBe(first.ConversationId);
        var plate = structured.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        (plate.FileName, plate.MediaType, plate.SizeBytes).ShouldBe(("plate.png", "image/png", 10L));
        await ReplyAsync(agent, structured, global::Domain.DTOs.ReplyContentType.Text, """{"mood":"calm"}""");
        await ReplyAsync(agent, structured, global::Domain.DTOs.ReplyContentType.StreamComplete, "");
        var second = await agent.Messages.FirstAsync(wait.Token);
        await ReplyAsync(agent, second, global::Domain.DTOs.ReplyContentType.Error, global::Domain.Monitor.RefusalAnswers.AgentGone("jonas"));
        await ReplyAsync(agent, second, global::Domain.DTOs.ReplyContentType.StreamComplete, "");

        await Assertions.Expect(page.Locator("#answer")).ToHaveTextAsync("Quietly. true");
        await Assertions.Expect(page.Locator("#value")).ToHaveTextAsync("calm false");
        await Assertions.Expect(page.Locator("#refused")).ToHaveTextAsync("true true AskError permission_denied false");
        await Assertions.Expect(page.Locator("#capped")).ToHaveTextAsync("true quota_exceeded false true");
        violations.ShouldBeEmpty();
        // Each refusal reaches the app's runtime errors as the ask it was, its author's report keeping the
        // envelope's words, never what was asked; caught by the app, it is no rejection besides.
        IReadOnlyList<System.Text.Json.Nodes.JsonNode> reported = [];
        await Eventually.Until(async () => (reported = await ErrorsAsync(server)).Count >= 2, "both failed asks are reported");
        reported.Count.ShouldBe(2);
        reported.ShouldAllBe(entry => entry["kind"]!.GetValue<string>() == "ask");
        var refused = reported.Single(entry => entry["ask"]!["envelope"]!["errorCode"]!.GetValue<string>() == "permission_denied");
        refused["message"]!.GetValue<string>().ShouldContain("'jonas'");
        reported.ShouldContain(entry => entry["ask"]!["envelope"]!["errorCode"]!.GetValue<string>() == "quota_exceeded");
        reported.ShouldAllBe(entry => !entry.ToJsonString().Contains("And now?") && !entry.ToJsonString().Contains("Once more?"));
    }

    private static Task ReplyAsync(
        global::Infrastructure.Clients.Channels.McpChannelConnection agent, global::Domain.DTOs.ChannelMessage turn,
        global::Domain.DTOs.ReplyContentType type, string content) =>
        agent.SendReplyAsync(new global::Domain.DTOs.Channel.SendReplyParams
        {
            ConversationId = turn.ConversationId,
            Content = content,
            ContentType = type,
            IsComplete = type == global::Domain.DTOs.ReplyContentType.StreamComplete,
            MessageId = "resp_1",
            TurnKey = turn.TurnKey,
            AgentInitiated = false
        }, CancellationToken.None);

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
        // Entries are newest first by arrival, which the page does not order: one without a read may come first.
        entries.Where(e => e["read"] is not null).ShouldContain(e => e["read"]!["envelope"]!["errorCode"]!.GetValue<string>() == "transient_dependency"
                                                                 && e["read"]!["operation"]!.GetValue<string>() == "query");
        entries.ShouldAllBe(e => e["route"]!.GetValue<string>() == "/today" && e["viewer"]!.GetValue<string>() == AppsServer.Her.UserId);
        string.Join("\n", entries).ShouldNotContain("her lentils");
        violations.ShouldBeEmpty();
    }

    // What was checked is what runs: a published page loads from a CDN only the packages, at the versions,
    // that ./publish found pinned on it — a pinned script and a file of its own package's version (a bundle's
    // chunk, an esm.sh stub's build) load, while a module's import of another package, or of the same package
    // at another version, from the very same CDN is refused by the policy and never leaves the browser.
    [Fact]
    public async Task APublishedPage_LoadsOnlyThePackagesItsPublishPinned_FromACdn()
    {
        const string umd = "globalThis.glanceUmd = 'drawn';";
        var integrity = "sha384-" + Convert.ToBase64String(System.Security.Cryptography.SHA384.HashData(System.Text.Encoding.UTF8.GetBytes(umd)));
        const string package = "https://cdn.jsdelivr.net/npm/glance@1.2.3";
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            $"""<!doctype html><html><head><script src="{package}/dist/glance.umd.js" integrity="{integrity}" crossorigin="anonymous"></script><script type="module" src="/main.js"></script></head><body></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js",
            $$"""
              document.body.dataset.umd = String(globalThis.glanceUmd);
              const tried = await Promise.all([
                '{{package}}/dist/chunk.mjs',
                'https://cdn.jsdelivr.net/npm/unpinned@1.0.0/index.mjs',
                'https://cdn.jsdelivr.net/npm/glance@9.9.9/dist/chunk.mjs'
              ].map(address => import(address).then(() => 'loaded', () => 'refused')));
              document.body.dataset.imports = tried.join(' ');
              """);
        (await server.ExecAsync(AppsServer.Her, "/glucose", "./publish"))["exitCode"]!.GetValue<int>().ShouldBe(0);
        var host = server.Published("glucose", id);
        var origin = $"http://{host}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var context = await browser.NewContextAsync();
        var fetched = new ConcurrentQueue<string>();
        await context.RouteAsync("https://cdn.jsdelivr.net/**", route =>
        {
            fetched.Enqueue(route.Request.Url);
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200, ContentType = "text/javascript", Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
                Body = route.Request.Url.EndsWith("glance.umd.js", StringComparison.Ordinal) ? umd : "export const loaded = true;"
            });
        });
        var page = await context.NewPageAsync();
        await SetAppCookieAsync(page, origin, server.SessionFor(AppsServer.Her, host));
        var violations = await ViolationsAsync(page);

        await page.GotoAsync(origin + "/");

        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-imports", "loaded refused refused");
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-umd", "drawn");
        fetched.ShouldBe([$"{package}/dist/glance.umd.js", $"{package}/dist/chunk.mjs"], ignoreOrder: true);
        violations.ShouldContain(v => v.Contains("unpinned@1.0.0"));
        violations.ShouldContain(v => v.Contains("glance@9.9.9"));
        violations.ShouldAllBe(v => v.Contains("unpinned@1.0.0") || v.Contains("glance@9.9.9"));
    }

    // An installed app's shell opens from what its worker kept: the app's own files come from the cache kept
    // for the served version, so a reload reaches the network for the page alone; a publish is a new version,
    // whose page loads its new files at once and whose worker drops the old cache; and with no network the
    // page opens from the kept shell. The household's reads and reports, and the sign-in, are never kept.
    [Fact]
    public async Task APublishedAppsShell_OpensFromItsWorkersCache_UntilAPublishReplacesIt_AndOffline()
    {
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html><head><script type="module" src="/main.js"></script></head><body></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", "document.body.dataset.version = '1'; globalThis.apps.read('/sugary/a.md').catch(() => {});");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var host = server.Published("glucose", id);
        var origin = $"http://{host}:{server.Settings.Listeners.Pages}";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchAsync(playwright);
        var context = await browser.NewContextAsync();
        var fromNetwork = 0;
        var offline = false;
        var refused = new ConcurrentQueue<string>();
        // Chromium routes a worker's own fetches too, so this is the network as the worker meets it.
        await context.RouteAsync($"{origin}/**", route =>
        {
            if (offline)
            {
                refused.Enqueue(route.Request.Url);
                return route.AbortAsync("internetdisconnected");
            }

            if (route.Request.Url.EndsWith("/main.js", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fromNetwork);
            }

            return route.ContinueAsync();
        });
        var page = await context.NewPageAsync();
        await SetAppCookieAsync(page, origin, server.SessionFor(AppsServer.Her, host));
        await page.GotoAsync(origin + "/");
        await WorkerActiveAsync(page);
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-version", "1");
        // The worker stores what it fetched after the page has it: wait for the store, not the page.
        await Eventually.Until(async () => (await KeptAsync(page)).Contains("/main.js"), "the worker keeps the app's file");
        var first = await CachesAsync(page);
        var fetched = fromNetwork;

        await page.ReloadAsync();

        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-version", "1");
        fromNetwork.ShouldBe(fetched, "the app's own file came from the worker's cache");

        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", "document.body.dataset.version = '2';", overwrite: true);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-version", "2");
        await Eventually.Until(async () => !(await CachesAsync(page)).Contains(first.Single()),
            "the new version's worker drops the old version's cache");
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-version", "2");
        await Eventually.Until(async () => await CachesAsync(page) is [var only] && only != first.Single()
                                           && await KeptAsync(page) is var kept && kept.Contains("/") && kept.Contains("/main.js"),
            "the new version's worker keeps the shell and its file in its own cache alone");

        offline = true;
        await page.GotoAsync(origin + "/week");

        await Assertions.Expect(page.Locator("body")).ToHaveAttributeAsync("data-version", "2");
        refused.ShouldContain(origin + "/week", "the page was asked of the network, which was gone");
        (await KeptAsync(page)).ShouldAllBe(path => !path.StartsWith("/_vfs/") && path != "/_errors" && path != "/_app/sign-in");
    }

    // Every path the origin's caches hold.
    private static Task<string[]> KeptAsync(IPage page) => page.EvaluateAsync<string[]>(
        "() => caches.keys().then(names => Promise.all(names.map(n => caches.open(n).then(c => c.keys())))).then(all => all.flat().map(r => new URL(r.url).pathname))");

    // A kept shell never stands in for the sign-in: once the app session has ended, a reload reaches the
    // server and is sent to sign in again, as the client library tells the person a reload does.
    [Fact]
    public async Task AnInstalledAppWhoseSessionEnded_IsSentToSignInOnReload_NotShownItsKeptShell()
    {
        await using var server = await AppsServer.StartAsync(s => s with { AppsDomain = LocalAppsDomain });
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<!doctype html><title>Glucose</title><h1>Glucose</h1>");
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
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("Glucose");

        server.Sessions.EndAll(AppsServer.Her);
        var signIn = page.WaitForRequestAsync(request => request.Url.StartsWith($"{AppsServer.MainHost}/accounts/app-sign-in", StringComparison.Ordinal));
        try
        {
            await page.ReloadAsync();
        }
        catch (PlaywrightException)
        {
            // The main host is no name this machine resolves: the request is the claim.
        }

        (await signIn).Url.ShouldContain(Uri.EscapeDataString(origin));
    }

    private static Task<string[]> CachesAsync(IPage page) => page.EvaluateAsync<string[]>("() => caches.keys()");

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
        page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null",
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