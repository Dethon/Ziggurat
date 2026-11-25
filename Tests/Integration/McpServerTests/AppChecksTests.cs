using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using McpServerApps.Checks;
using McpServerApps.Settings;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A check as the agent runs one: `./check` in an app's directory on the /apps mount, the preview loaded in
// the apps server's own headless Chromium as the person whose turn ran it, and what it saw read back from
// /apps/<slug>/.checks/latest/ with the file tools. Real Chromium, in process, on the fixture's apps
// domain (apps.test, no .localhost name), so the secure context the check gives the preview is its own.
// Launched here unless a case names a browser server: the deployment's runs in a container of its own.
public sealed class AppChecksTests(PlaywrightBrowserServer browserServer) : IClassFixture<PlaywrightBrowserServer>
{
    private static readonly string[] _defaultShots = ["1280x800.png", "390x844.png"];

    // Far past what a check takes, so a loaded machine never reads as a check stopped at its deadline:
    // only the deadline's own case runs to one.
    private static readonly TimeSpan _generous = TimeSpan.FromMinutes(2);

    private static Task<AppsServer> StartAsync(Func<AppsSettings, AppsSettings>? configure = null) =>
        AppsServer.StartAsync(s =>
        {
            var generous = s with { Checks = s.Checks with { Deadline = _generous } };
            return configure?.Invoke(generous) ?? generous;
        });

    [Fact]
    public async Task ACheck_ShowsThePreviewAtAPhoneAndADesktop_AndReportsEach()
    {
        await using var server = await StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            "<!doctype html><html lang=\"en\"><head><title>Glucose</title></head><body><h1>Glucose today</h1></body></html>");

        var ran = await server.ExecAsync(AppsServer.Her, "/glucose", "./check");

        ran["exitCode"]!.GetValue<int>().ShouldBe(0, ran.ToJsonString());
        ran["stdout"]!.GetValue<string>().ShouldContain("/glucose/.checks/latest/");
        (await server.GlobAsync(AppsServer.Her, "*", "/glucose/.checks/latest"))
            .ShouldBe([.. _defaultShots.Select(s => $"/glucose/.checks/latest/{s}"), "/glucose/.checks/latest/report.json"]);
        foreach (var shot in _defaultShots)
        {
            (await BytesAsync(server, $"/glucose/.checks/latest/{shot}"))[..8]
                .ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, shot);
        }

        var report = await ReportAsync(server, "glucose");
        report["deadlineHit"]!.GetValue<bool>().ShouldBeFalse();
        var viewports = report["viewports"]!.AsArray();
        viewports.Select(v => v!["screenshot"]!.GetValue<string>()).ShouldBe(["390x844.png", "1280x800.png"]);
        viewports.ShouldAllBe(v => v!["accessibility"]!.GetValue<string>().Contains("heading \"Glucose today\" [level=1]"));
    }

    // What a page gets wrong reaches the agent before the person: an error it logs or throws, a request
    // answered 404, and what its content policy refused, each with what it was about.
    [Fact]
    public async Task ACheck_ReportsConsoleErrors_FailedRequests_AndPolicyViolations()
    {
        await using var server = await StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html lang="en"><head><title>Glucose</title><script type="module" src="/main.js"></script></head><body><h1>Glucose</h1><img alt="chart" src="https://charts.example.org/today.png"></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js",
            """
            console.error('the glucose curve did not draw');
            await fetch('/data/missing.json');
            throw new Error('nothing to show');
            """);

        await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");

        var viewport = (await ReportAsync(server, "glucose"))["viewports"]!.AsArray().Single()!;
        var console = viewport["console"]!.AsArray();
        console.ShouldContain(c => c!["type"]!.GetValue<string>() == "error"
                                   && c["text"]!.GetValue<string>() == "the glucose curve did not draw");
        console.ShouldContain(c => c!["type"]!.GetValue<string>() == "uncaught"
                                   && c["text"]!.GetValue<string>().Contains("nothing to show"));
        viewport["failedRequests"]!.AsArray().ShouldContain(r => r!["url"]!.GetValue<string>().EndsWith("/data/missing.json")
                                                                 && r["status"]!.GetValue<int>() == 404);
        var violation = viewport["policyViolations"]!.AsArray().ShouldHaveSingleItem()!;
        violation["directive"]!.GetValue<string>().ShouldBe("img-src");
        violation["blockedUri"]!.GetValue<string>().ShouldBe("https://charts.example.org/today.png");
    }

    // An app's own assertions run in the page, against what it rendered: one that returns passes with
    // what it returned, one that throws fails with what it threw, and one that does not parse fails too.
    // Their outcome is the exit code.
    [Fact]
    public async Task EachAssertionFile_RunsInThePage_AndReportsPassOrFailWithItsMessage()
    {
        await using var server = await StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html lang="en"><head><title>Glucose</title><script type="module" src="/main.js"></script></head><body><ul id="meals"></ul></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js",
            "document.getElementById('meals').innerHTML = '<li>lentils</li><li>paella</li>';");
        await server.CreateAsync(AppsServer.Her, "/glucose/checks/meals.js",
            """
            const meals = document.querySelectorAll('#meals li').length;
            if (meals !== 2) throw new Error(`expected 2 meals, saw ${meals}`);
            return `${meals} meals listed`;
            """);
        await server.CreateAsync(AppsServer.Her, "/glucose/checks/title.js",
            """
            if (document.title !== 'Glucose today') throw new Error(`the title is "${document.title}"`);
            """);
        await server.CreateAsync(AppsServer.Her, "/glucose/checks/typo.js", "if (");

        var ran = await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");

        ran["exitCode"]!.GetValue<int>().ShouldBe(1, ran.ToJsonString());
        var assertions = (await ReportAsync(server, "glucose"))["viewports"]![0]!["assertions"]!.AsArray();
        assertions.Select(a => (a!["file"]!.GetValue<string>(), a["passed"]!.GetValue<bool>())).ShouldBe(
            [("checks/meals.js", true), ("checks/title.js", false), ("checks/typo.js", false)]);
        assertions[0]!["message"]!.GetValue<string>().ShouldBe("2 meals listed");
        assertions[1]!["message"]!.GetValue<string>().ShouldBe("the title is \"Glucose\"");
        assertions[2]!["message"]!.GetValue<string>().ShouldContain("SyntaxError");
    }

    // A check's page reads the household as the person whose turn ran it, through /_vfs/* as an installed
    // app does: his own meals, not the other member's, on nothing but his call's own token.
    [Fact]
    public async Task ACheck_ReadsAsThePersonWhoseTurnRanIt()
    {
        await using var sugary = await SugaryServer.StartAsync(sugaryPeople: new HouseholdSugaryPeople(
        [
            new SugaryPerson(AppsServer.Her.UserId, "Her", ReadsEveryLog: false),
            new SugaryPerson(AppsServer.Him.UserId, "Him", ReadsEveryLog: false)
        ]));
        await SaveMealAsync(sugary, AppsServer.Her, "her lentils");
        await SaveMealAsync(sugary, AppsServer.Him, "his paella");
        var households = new AgentHouseholds();
        households.Add(AppsServer.Him.TenantId, new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [sugary.Endpoint], EnabledFeatures = ["filesystem"],
            WhitelistPatterns = ["domain__filesystem__*"]
        });
        await using var agent = await AppReadsAgent.StartAsync(households, new SharedServers { Sugary = sugary.Endpoint });
        await using var server = await StartAsync(s => s with { AgentUrl = agent.Address });
        await server.AppAsync(AppsServer.Him, "meals");
        await server.CreateAsync(AppsServer.Him, "/meals/index.html",
            """<!doctype html><html lang="en"><head><title>Meals</title><script type="module" src="/main.js"></script></head><body><p id="meal">loading</p></body></html>""");
        await server.CreateAsync(AppsServer.Him, "/meals/main.js",
            """
            const meals = await apps.query('/sugary', 'select notes from meals');
            document.getElementById('meal').textContent = meals.rows.map(row => row[0]).join(', ');
            """);

        var ran = await server.ExecAsync(AppsServer.Him, "/meals", "./check --viewport 800x600");

        ran["exitCode"]!.GetValue<int>().ShouldBe(0, ran.ToJsonString());
        var viewport = (await ReportAsync(server, "meals", AppsServer.Him))["viewports"]![0]!;
        viewport["accessibility"]!.GetValue<string>().ShouldContain("paragraph: his paella", Case.Sensitive, viewport.ToJsonString());
        viewport["failedRequests"]!.AsArray().ShouldBeEmpty();
    }

    // The check's browser sits on the deployment's network, and what it shows comes back to the agent as a
    // screenshot: a page reaches only its own origin, the CDNs and its manifest's connect origins. A
    // navigation anywhere else — an internal service here — is aborted and reported, never shown.
    [Fact]
    public async Task ACheck_NeverShowsAPageOutsideWhatTheAppMayReach()
    {
        var (port, inside) = await TestPort.StartWebAsync(port =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port));
            var built = builder.Build();
            built.MapGet("/", () => Results.Content("<h1>the deployment's secrets</h1>", "text/html"));
            return built;
        });
        await using var _ = inside;
        await using var server = await StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html lang="en"><head><title>Glucose</title><script type="module" src="/main.js"></script></head><body><h1>Glucose</h1></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", $"location.href = 'http://127.0.0.1:{port}/';");

        await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");

        var viewport = (await ReportAsync(server, "glucose"))["viewports"]![0]!;
        viewport["accessibility"]?.GetValue<string>().ShouldNotContain("secrets");
        viewport["failedRequests"]!.AsArray().ShouldContain(r => r!["url"]!.GetValue<string>() == $"http://127.0.0.1:{port}/"
                                                                 && r["error"]!.GetValue<string>().StartsWith("blocked"));
    }

    // An origin the app may reach can still send the browser on: a redirect from one (a CDN here) to an
    // internal service is followed past the request's own check, so what the check captured is kept only
    // while the page is still the preview, and otherwise dropped and said.
    [Fact]
    public async Task ACheck_NeverShowsWhereAnAllowedOriginRedirectsTo()
    {
        var (insidePort, inside) = await TestPort.StartWebAsync(port =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port));
            var built = builder.Build();
            built.MapGet("/", () => Results.Content("<h1>the deployment's secrets</h1>", "text/html"));
            return built;
        });
        await using var _ = inside;
        var (cdnPort, cdn) = await TestPort.StartWebAsync(port =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port));
            var built = builder.Build();
            built.MapGet("/", () => Results.Redirect($"http://127.0.0.1:{insidePort}/"));
            return built;
        });
        await using var __ = cdn;
        var cdnOrigin = $"http://localhost:{cdnPort}";
        await using var server = await StartAsync(s => s with { Cdns = [.. s.Cdns, cdnOrigin] });
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html lang="en"><head><title>Glucose</title><script type="module" src="/main.js"></script></head><body><h1>Glucose</h1></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", $"location.href = '{cdnOrigin}/';");

        await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");

        var viewport = (await ReportAsync(server, "glucose"))["viewports"]![0]!;
        viewport["accessibility"]?.GetValue<string>().ShouldNotContain("secrets");
        viewport["screenshot"].ShouldBeNull(viewport.ToJsonString());
        viewport["error"]!.GetValue<string>().ShouldContain("left the preview");
        (await server.GlobAsync(AppsServer.Her, "*", "/glucose/.checks/latest")).ShouldBe(["/glucose/.checks/latest/report.json"]);
    }

    // Each check runs a browser of its own, so a household runs one at a time and the server a few: one
    // past either limit is refused at once, naming the limit and its value, never queued behind the others.
    // The running checks are held open by a read the agent has not answered yet.
    [Fact]
    public async Task AChecksPastTheHouseholdsOrTheServersLimit_AreRefused_NotQueued()
    {
        var third = new Account("household-third", "user-third");
        await using var agent = await HeldAgent.StartAsync();
        await using var server = await StartAsync(s => s with
        {
            AgentUrl = agent.Address,
            Checks = s.Checks with { PerHousehold = 1, Total = 2 }
        });
        foreach (var person in new[] { AppsServer.Her, AppsServer.Him, AppsServer.Neighbour, third })
        {
            await HoldingAppAsync(server, person);
        }

        var hers = server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");
        await Eventually.Until(() => agent.Held == 1, "her check's page is reading");

        var his = await server.ExecAsync(AppsServer.Him, "/glucose", "./check");
        his["errorCode"]!.GetValue<string>().ShouldBe("permission_denied", his.ToJsonString());
        his["message"]!.GetValue<string>().ShouldContain("concurrent checks per household is 1");

        var neighbours = server.ExecAsync(AppsServer.Neighbour, "/glucose", "./check --viewport 800x600");
        await Eventually.Until(() => agent.Held == 2, "the neighbour's check's page is reading");
        var thirds = await server.ExecAsync(third, "/glucose", "./check");
        thirds["errorCode"]!.GetValue<string>().ShouldBe("permission_denied", thirds.ToJsonString());
        thirds["message"]!.GetValue<string>().ShouldContain("concurrent checks on this server is 2");

        agent.Release();
        (await hers)["exitCode"]!.GetValue<int>().ShouldBe(0);
        (await neighbours)["exitCode"]!.GetValue<int>().ShouldBe(0);
    }

    // A page that never finishes is stopped at the check's deadline, its browser closed, and the report
    // and the answer both say so.
    [Fact]
    public async Task ACheckPastItsDeadline_IsStopped_AndItsReportSaysSo()
    {
        await using var server = await AppsServer.StartAsync(s => s with
        {
            Checks = s.Checks with { Deadline = TimeSpan.FromSeconds(3) }
        });
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<!doctype html><title>Glucose</title><h1>Glucose</h1>");
        await server.CreateAsync(AppsServer.Her, "/glucose/checks/forever.js", "await new Promise(() => {});");

        var ran = await server.ExecAsync(AppsServer.Her, "/glucose", "./check");

        ran["exitCode"]!.GetValue<int>().ShouldBe(1, ran.ToJsonString());
        ran["stdout"]!.GetValue<string>().ShouldContain("stopped at its deadline of 3 s");
        var report = await ReportAsync(server, "glucose");
        report["deadlineHit"]!.GetValue<bool>().ShouldBeTrue();
        report["viewports"]!.AsArray().ShouldAllBe(v => v!["assertions"]!.AsArray().Count == 0);
    }

    // In the deployment the browser runs in a container of its own, on a Playwright browser server that
    // launches it with what the check sends: the same browser, reaching this server's pages listener
    // where it is told, the preview still a secure context with its session.
    [Fact]
    public async Task ACheck_RunsOnTheBrowserServerItIsGiven_AsTheBrowserItWouldLaunch()
    {
        await using var server = await StartAsync(s => s with
        {
            Checks = s.Checks with
            {
                Browser = new CheckBrowserSettings { Endpoint = browserServer.Endpoint, Pages = $"localhost:{s.Listeners.Pages}" }
            }
        });
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            "<!doctype html><html lang=\"en\"><head><title>Glucose</title></head><body><h1>Glucose today</h1></body></html>");
        await server.CreateAsync(AppsServer.Her, "/glucose/checks/secure.js",
            "if (!isSecureContext) throw new Error('not a secure context'); return 'secure';");

        var ran = await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");

        ran["exitCode"]!.GetValue<int>().ShouldBe(0, ran.ToJsonString());
        var viewport = (await ReportAsync(server, "glucose"))["viewports"]![0]!;
        viewport["screenshot"]!.GetValue<string>().ShouldBe("800x600.png");
        viewport["accessibility"]!.GetValue<string>().ShouldContain("heading \"Glucose today\" [level=1]");
        viewport["assertions"]![0]!["message"]!.GetValue<string>().ShouldBe("secure");
    }

    // A browser server that does not answer is a check that could not run its browser, said in the report
    // and the exit code, never a launch here instead.
    [Fact]
    public async Task ACheck_WhoseBrowserServerDoesNotAnswer_SaysItsBrowserCouldNotRun()
    {
        await using var server = await StartAsync(s => s with
        {
            Checks = s.Checks with { Browser = new CheckBrowserSettings { Endpoint = $"ws://127.0.0.1:{DeadEnd.Port}/" } }
        });
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<!doctype html><title>Glucose</title><h1>Glucose</h1>");

        var ran = await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");

        ran["exitCode"]!.GetValue<int>().ShouldBe(1, ran.ToJsonString());
        var report = await ReportAsync(server, "glucose");
        report["error"]!.GetValue<string>().ShouldStartWith("the browser could not run the check");
        report["viewports"]!.AsArray().ShouldBeEmpty();
    }

    // The agent's code runs in the page, so the page runs in Chromium's own sandbox wherever the browser
    // is: launched here, or on a browser server, which drops a sandbox it is not asked for.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChecksBrowser_RunsInChromiumsSandbox(bool onAServer)
    {
        var settings = new AppsSettings { AppsDomain = AppsServer.AppsDomain };
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await CheckBrowser.OpenAsync(playwright,
            new CheckBrowserSettings { Endpoint = onAServer ? browserServer.Endpoint : "" },
            CheckBrowser.Options(settings, $"http://glucose-abc123-preview.{settings.AppsDomain}"));
        var page = await browser.NewPageAsync();

        await page.GotoAsync("chrome://sandbox");

        (await page.Locator("body").InnerTextAsync()).ShouldContain("You are adequately sandboxed.");
    }

    // The viewports and the route are the agent's to name, and what a check leaves is the server's: the
    // last check's files replaced whole by the next, never writable, never part of the app.
    [Fact]
    public async Task ACheck_OpensTheRouteAtTheViewportsNamed_AndItsOutputIsTheServersAlone()
    {
        await using var server = await StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html lang="en"><head><title>Glucose</title><script type="module" src="/main.js"></script></head><body><h1 id="route"></h1></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", "document.getElementById('route').textContent = location.pathname;");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./check");

        var ran = await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 600x400 --path /week");

        ran["exitCode"]!.GetValue<int>().ShouldBe(0, ran.ToJsonString());
        (await server.GlobAsync(AppsServer.Her, "*", "/glucose/.checks/latest"))
            .ShouldBe(["/glucose/.checks/latest/600x400.png", "/glucose/.checks/latest/report.json"]);
        var report = await ReportAsync(server, "glucose");
        report["path"]!.GetValue<string>().ShouldBe("/week");
        report["viewports"]![0]!["accessibility"]!.GetValue<string>().ShouldContain("heading \"/week\"");
        var written = await server.CreateAsync(AppsServer.Her, "/glucose/.checks/latest/report.json", "{}", overwrite: true);
        written["errorCode"]!.GetValue<string>().ShouldBe("permission_denied", written.ToJsonString());
        (await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport huge"))["exitCode"]!.GetValue<int>().ShouldBe(2);
        (await server.GlobAsync(AppsServer.Her, "**", "/glucose")).ShouldNotContain(p => p.Contains(".checks/run-"));
    }

    // An app whose one assertion waits on a read of the household, which HeldAgent answers only when told.
    private static async Task HoldingAppAsync(AppsServer server, Account person)
    {
        await server.AppAsync(person, "glucose");
        await server.CreateAsync(person, "/glucose/index.html", "<!doctype html><title>Glucose</title><h1>Glucose</h1>");
        await server.CreateAsync(person, "/glucose/checks/reads.js", "await apps.read('/sugary/README.md'); return 'read';");
    }

    // The agent's read route, holding every read until released: what keeps a check running.
    private sealed class HeldAgent : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        private HeldAgent(WebApplication app, int port)
        {
            _app = app;
            Address = $"http://127.0.0.1:{port}";
        }

        public string Address { get; }

        public int Held => Volatile.Read(ref _held);

        public static async Task<HeldAgent> StartAsync()
        {
            HeldAgent? agent = null;
            var (port, app) = await TestPort.StartWebAsync(port =>
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port));
                var built = builder.Build();
                built.MapPost($"{AppReads.AgentPath}/{{operation}}", async () =>
                {
                    Interlocked.Increment(ref agent!._held);
                    await agent._released.Task;
                    return Results.Json(new { filePath = "/sugary/README.md", content = "1: Sugary" });
                });
                return built;
            });
            agent = new HeldAgent(app, port);
            return agent;
        }

        public void Release() => _released.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Release();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static async Task<JsonNode> ReportAsync(AppsServer server, string slug, Account? person = null) =>
        JsonNode.Parse(await BytesAsync(server, $"/{slug}/.checks/latest/report.json", person))!;

    // A file of the mount whole, as a copy streams it: the byte read, a chunk at a time.
    private static async Task<byte[]> BytesAsync(AppsServer server, string path, Account? person = null)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var chunk = await server.CallAsync("fs_blob_read", new Dictionary<string, object?>
            {
                ["path"] = path, ["offset"] = bytes.Count, ["length"] = 256 * 1024
            }, AppsServer.TurnOf(person ?? AppsServer.Her));
            var read = Convert.FromBase64String(chunk["contentBase64"]?.GetValue<string>()
                                                ?? throw new InvalidOperationException(chunk.ToJsonString()));
            bytes.AddRange(read);
            if (chunk["eof"]!.GetValue<bool>() || read.Length == 0)
            {
                return [.. bytes];
            }
        }
    }

    private static async Task SaveMealAsync(SugaryServer server, Account person, string notes)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        db.Meals.Add(new Meal
        {
            Id = Guid.NewGuid(), EatenAt = DateTimeOffset.Parse("2026-10-11T12:00:00Z"), Notes = notes, Outcome = MealOutcome.High,
            FirstEstimate = new Interval(30, 40, 55), Items = [], CarbsLow = 30, Carbs = 40, CarbsHigh = 55
        });
        await db.SaveChangesAsync();
    }
}