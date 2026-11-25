using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A check as the agent runs one: `./check` in an app's directory on the /apps mount, the preview loaded in
// the apps server's own headless Chromium as the person whose turn ran it, and what it saw read back from
// /apps/<slug>/.checks/latest/ with the file tools. Real Chromium, in process, on the fixture's apps
// domain (apps.test, no .localhost name), so the secure context the check gives the preview is its own.
public sealed class AppChecksTests
{
    private static readonly string[] _defaultShots = ["1280x800.png", "390x844.png"];

    [Fact]
    public async Task ACheck_ShowsThePreviewAtAPhoneAndADesktop_AndReportsEach()
    {
        await using var server = await AppsServer.StartAsync();
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
        await using var server = await AppsServer.StartAsync();
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
        await using var server = await AppsServer.StartAsync();
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
        await using var server = await AppsServer.StartAsync(s => s with { AgentUrl = agent.Address });
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
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html",
            """<!doctype html><html lang="en"><head><title>Glucose</title><script type="module" src="/main.js"></script></head><body><h1>Glucose</h1></body></html>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/main.js", $"location.href = 'http://127.0.0.1:{port}/';");

        await server.ExecAsync(AppsServer.Her, "/glucose", "./check --viewport 800x600");

        var viewport = (await ReportAsync(server, "glucose"))["viewports"]![0]!;
        viewport["accessibility"]!.GetValue<string>().ShouldNotContain("secrets");
        viewport["failedRequests"]!.AsArray().ShouldContain(r => r!["url"]!.GetValue<string>() == $"http://127.0.0.1:{port}/"
                                                                 && r["error"]!.GetValue<string>().StartsWith("blocked"));
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