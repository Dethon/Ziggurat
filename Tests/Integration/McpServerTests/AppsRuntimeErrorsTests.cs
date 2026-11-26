using System.Net;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// When a person says an app is broken on their phone, the agent reads why: the client library posts each
// runtime error to /_errors on the app's origin, and the apps server keeps the newest of them per app,
// shown read-only at /apps/<slug>/errors newest first. Who reported, when, from which browser and on
// which origin is the server's word; only what went wrong is the page's, held to a strict shape.
public sealed class AppsRuntimeErrorsTests : IAsyncLifetime
{
    private AppsServer _apps = null!;
    private string _id = null!;

    private const string Uncaught =
        """{"kind":"error","message":"TypeError: rows is undefined","stack":"draw@/main.js:3:7","route":"/today"}""";

    public async Task InitializeAsync()
    {
        _apps = await AppsServer.StartAsync();
        _id = AppsServer.IdIn(await _apps.AppAsync(AppsServer.Her, "glucose"));
        await _apps.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Glucose</h1>");
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
    }

    public async Task DisposeAsync() => await _apps.DisposeAsync();

    [Fact]
    public async Task AViewersReport_IsKept_WithWhoWhenAndWhereStampedByTheServer()
    {
        var host = _apps.Published("glucose", _id);

        using var reported = await _apps.ReportAsync(host, Uncaught, _apps.SessionFor(AppsServer.Her, host),
            userAgent: "Mozilla/5.0 (Linux; Android 15) Pixel");

        reported.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reported.Content.ReadAsStringAsync());
        var entry = (await EntriesAsync()).ShouldHaveSingleItem();
        entry["kind"]!.GetValue<string>().ShouldBe("error");
        entry["message"]!.GetValue<string>().ShouldBe("TypeError: rows is undefined");
        entry["stack"]!.GetValue<string>().ShouldBe("draw@/main.js:3:7");
        entry["route"]!.GetValue<string>().ShouldBe("/today");
        entry["userAgent"]!.GetValue<string>().ShouldBe("Mozilla/5.0 (Linux; Android 15) Pixel");
        entry["viewer"]!.GetValue<string>().ShouldBe(AppsServer.Her.UserId);
        entry["origin"]!.GetValue<string>().ShouldBe("published");
        DateTimeOffset.Parse(entry["time"]!.GetValue<string>()).ShouldBe(_apps.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
    }

    // Only a signed-in viewer of that app reports, and only on it: nobody, a member who may not open the
    // app, and a session of another of her apps are the one 404 a page gets, and nothing is kept anywhere.
    [Fact]
    public async Task AReportByNobodyByAStrangerOrFromAnotherAppsSession_IsTheOne404_AndKeptNowhere()
    {
        var other = AppsServer.IdIn(await _apps.AppAsync(AppsServer.Her, "meals", name: "Meals"));
        await _apps.CreateAsync(AppsServer.Her, "/meals/index.html", "<h1>Meals</h1>");
        await _apps.ExecAsync(AppsServer.Her, "/meals", "./publish");
        var host = _apps.Published("glucose", _id);
        var page = await _apps.GetAsync(host, "/missing.css", _apps.SessionFor(AppsServer.Him, host));

        using var signedOut = await _apps.ReportAsync(host, Uncaught, session: null);
        using var stranger = await _apps.ReportAsync(host, Uncaught, _apps.SessionFor(AppsServer.Him, host));
        using var otherApps = await _apps.ReportAsync(host, Uncaught, _apps.SessionFor(AppsServer.Her, _apps.Published("meals", other)));

        foreach (var answer in new[] { signedOut, stranger, otherApps })
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await answer.Content.ReadAsStringAsync()).ShouldBe(await page.Content.ReadAsStringAsync());
        }

        (await _apps.GlobAsync(AppsServer.Her, "**/errors")).ShouldBeEmpty();
    }

    // Sibling apps share the apps domain, so a page of another app could post here with this origin's
    // cookie riding along: a report is taken only from a page of this very origin.
    [Fact]
    public async Task AReportFromAnotherOriginsPage_IsRefused_AndNotKept()
    {
        var host = _apps.Published("glucose", _id);

        using var answer = await _apps.ReportAsync(host, Uncaught, _apps.SessionFor(AppsServer.Her, host),
            origin: $"https://other-abc123.{AppsServer.AppsDomain}");

        answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _apps.GlobAsync(AppsServer.Her, "**/errors")).ShouldBeEmpty();
    }

    // The shape is the server's, never the page's: a field it does not name, a kind it does not know, an
    // oversized message or stack, a route carrying a query, or a body past the cap is refused whole.
    [Theory]
    [InlineData("""{"kind":"error","message":"m","route":"/","rows":[["her lentils"]]}""")]
    [InlineData("""{"kind":"error","message":"m","route":"/","viewer":"user-him"}""")]
    [InlineData("""{"kind":"panic","message":"m","route":"/"}""")]
    [InlineData("""{"kind":"error","route":"/"}""")]
    [InlineData("""{"kind":"error","message":"m","route":"/?glucose=180"}""")]
    [InlineData("""{"kind":"read","message":"m","route":"/"}""")]
    [InlineData("""{"kind":"error","message":"m","route":"/","read":{"operation":"query","path":"/sugary","envelope":{"errorCode":"permission_denied","message":"no"}}}""")]
    [InlineData("""{"kind":"read","message":"m","route":"/","read":{"operation":"query","path":"/sugary","envelope":{"errorCode":"permission_denied","message":"no","rows":[1]}}}""")]
    [InlineData("""{"kind":"read","message":"m","route":"/","read":{"operation":"write","path":"/sugary","envelope":{"errorCode":"permission_denied","message":"no"}}}""")]
    [InlineData("""{"kind":"read","message":"m","route":"/","read":{"operation":"query","path":"/sugary","envelope":{"errorCode":"made_up","message":"no"}}}""")]
    [InlineData("[1,2,3]")]
    public async Task AReportOutsideTheShape_IsRefused_AndNotKept(string body)
    {
        var host = _apps.Published("glucose", _id);

        using var answer = await _apps.ReportAsync(host, body, _apps.SessionFor(AppsServer.Her, host));

        answer.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _apps.GlobAsync(AppsServer.Her, "**/errors")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("message", AppErrorReport.MaxMessage)]
    [InlineData("stack", AppErrorReport.MaxStack)]
    [InlineData("route", AppErrorReport.MaxRoute)]
    public async Task AFieldPastItsCap_IsRefused_AndOneAtItsCapIsKept(string field, int cap)
    {
        var host = _apps.Published("glucose", _id);
        var session = _apps.SessionFor(AppsServer.Her, host);
        string Body(int length) => new JsonObject
        {
            ["kind"] = "error", ["message"] = "m", ["route"] = "/", [field] = field == "route" ? "/" + new string('a', length - 1) : new string('a', length)
        }.ToJsonString();

        using var over = await _apps.ReportAsync(host, Body(cap + 1), session);
        using var at = await _apps.ReportAsync(host, Body(cap), session);

        over.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        at.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await EntriesAsync()).ShouldHaveSingleItem()[field]!.GetValue<string>().Length.ShouldBe(cap);
    }

    // A failed or refused read says what was asked and what the household answered; the envelope's
    // retryability is this side's taxonomy's, whatever the page sent.
    [Fact]
    public async Task ARefusedRead_IsKept_WithItsPathAndEnvelope()
    {
        var host = _apps.Published("glucose", _id);

        using var reported = await _apps.ReportAsync(host,
            """{"kind":"read","message":"Not in reads","route":"/","read":{"operation":"read","path":"/vault/a.md","envelope":{"errorCode":"transient_dependency","message":"Not in reads","hint":"Add /vault","retryable":false}}}""",
            _apps.SessionFor(AppsServer.Her, host));

        reported.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reported.Content.ReadAsStringAsync());
        var read = (await EntriesAsync()).ShouldHaveSingleItem()["read"]!;
        read["operation"]!.GetValue<string>().ShouldBe("read");
        read["path"]!.GetValue<string>().ShouldBe("/vault/a.md");
        read["envelope"]!["errorCode"]!.GetValue<string>().ShouldBe("transient_dependency");
        read["envelope"]!["hint"]!.GetValue<string>().ShouldBe("Add /vault");
        read["envelope"]!["retryable"]!.GetValue<bool>().ShouldBeTrue();
    }

    // The newest Errors:Kept are kept, newest first; an older one drops as a newer arrives.
    [Fact]
    public async Task OnlyTheNewestKeptAreKept_NewestFirst()
    {
        await using var apps = await AppsServer.StartAsync(s => s with { Errors = s.Errors with { Kept = 3 } });
        var id = AppsServer.IdIn(await apps.AppAsync(AppsServer.Her, "glucose"));
        var host = apps.Preview("glucose", id);
        var session = apps.SessionFor(AppsServer.Her, host);

        foreach (var n in Enumerable.Range(1, 5))
        {
            using var reported = await apps.ReportAsync(host, $$"""{"kind":"error","message":"error {{n}}","route":"/"}""", session);
            reported.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await EntriesAsync(apps)).Select(e => e["message"]!.GetValue<string>()).ShouldBe(["error 5", "error 4", "error 3"]);
    }

    // One broken loop cannot flood the server: a session past its reports a minute is answered 429 and
    // its reports are dropped until the minute is out.
    [Fact]
    public async Task ASessionPastItsReportsAMinute_IsAnswered429_AndDropped()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-11-25T10:00:00Z"));
        await using var apps = await AppsServer.StartAsync(
            s => s with { Errors = s.Errors with { PerSessionPerMinute = 2 } },
            services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        var id = AppsServer.IdIn(await apps.AppAsync(AppsServer.Her, "glucose"));
        var host = apps.Preview("glucose", id);
        var session = apps.SessionFor(AppsServer.Her, host);
        var another = apps.SessionFor(AppsServer.Her, host);

        var answers = new List<HttpStatusCode>();
        foreach (var n in Enumerable.Range(1, 3))
        {
            using var reported = await apps.ReportAsync(host, $$"""{"kind":"error","message":"loop {{n}}","route":"/"}""", session);
            answers.Add(reported.StatusCode);
        }

        using var otherSession = await apps.ReportAsync(host, """{"kind":"error","message":"elsewhere","route":"/"}""", another);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var nextMinute = await apps.ReportAsync(host, """{"kind":"error","message":"loop 4","route":"/"}""", session);

        answers.ShouldBe([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests]);
        otherSession.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        nextMinute.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await EntriesAsync(apps)).Select(e => e["message"]!.GetValue<string>()).ShouldBe(["loop 4", "elsewhere", "loop 2", "loop 1"]);
    }

    // The errors are the server's: listed beside the app's files, read like any text file, never written,
    // edited, moved or deleted by the agent, never committed, and spending nothing of the app's quota.
    [Fact]
    public async Task TheErrorsFile_IsListedAndRead_ButNeverWrittenCommittedOrCounted()
    {
        var host = _apps.Published("glucose", _id);
        var app = (await _apps.Store.FindAsync(_id, CancellationToken.None))!;
        var before = _apps.Store.SizeOf(app);
        using var reported = await _apps.ReportAsync(host, Uncaught, _apps.SessionFor(AppsServer.Her, host));

        (await _apps.GlobAsync(AppsServer.Her, "*", "/glucose")).ShouldContain("/glucose/errors");
        var refusals = new[]
        {
            await _apps.CreateAsync(AppsServer.Her, "/glucose/errors", "[]", overwrite: true),
            await _apps.CallAsync("fs_edit", new Dictionary<string, object?>
            {
                ["path"] = "/glucose/errors",
                ["edits"] = new[] { new { oldString = "rows", newString = "data" } }
            }, AppsServer.TurnOf(AppsServer.Her)),
            await _apps.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose/errors" }, AppsServer.TurnOf(AppsServer.Her)),
            await _apps.CallAsync("fs_move", new Dictionary<string, object?>
            {
                ["sourcePath"] = "/glucose/errors", ["destinationPath"] = "/glucose/kept.txt"
            }, AppsServer.TurnOf(AppsServer.Her))
        };

        refusals.ShouldAllBe(refused => refused["errorCode"]!.GetValue<string>() == "permission_denied");
        (await EntriesAsync()).ShouldHaveSingleItem()["message"]!.GetValue<string>().ShouldBe("TypeError: rows is undefined");
        _apps.Store.SizeOf(app).ShouldBe(before);
        await _apps.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Glucose, again</h1>", overwrite: true);
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        (await _apps.GetAsync(host, "/errors", _apps.SessionFor(AppsServer.Her, host))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The operator sees a household's broken app where every household error is seen: an ErrorEvent under
    // the app's own household, never one the page named.
    [Fact]
    public async Task AReport_IsAHouseholdErrorTheOperatorSees()
    {
        var metrics = new Tests.Unit.RecordingMetricsPublisher();
        await using var apps = await AppsServer.StartAsync(services: services => services.AddSingleton<IMetricsPublisher>(metrics));
        var id = AppsServer.IdIn(await apps.AppAsync(AppsServer.Her, "glucose"));
        var host = apps.Preview("glucose", id);

        using var reported = await apps.ReportAsync(host, Uncaught, apps.SessionFor(AppsServer.Her, host));

        reported.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var error = metrics.Published.OfType<ErrorEvent>().ShouldHaveSingleItem();
        error.TenantId.ShouldBe(AppsServer.Her.TenantId);
        error.Service.ShouldBe("mcp-apps");
        error.ErrorType.ShouldBe(AppErrors.ErrorType);
        error.Message.ShouldContain($"glucose-{id}-preview");
        error.Message.ShouldContain("TypeError: rows is undefined");
        error.Message.ShouldNotContain("draw@/main.js");
    }

    // A check's page reports through its own session too, and is answered as kept, but nothing is: the
    // check's report already shows the agent those page errors, a check is a new page per viewport and run,
    // so keeping them would push people's reports out of the newest Errors:Kept, and nobody met them, so
    // the operator is not told. Nor does a check spend its session's minute.
    [Fact]
    public async Task AChecksReport_IsAnsweredButKeptNowhere_AndNeverReachesTheOperator()
    {
        var metrics = new Tests.Unit.RecordingMetricsPublisher();
        await using var apps = await AppsServer.StartAsync(
            s => s with { Errors = s.Errors with { PerSessionPerMinute = 1 } },
            services => services.AddSingleton<IMetricsPublisher>(metrics));
        var id = AppsServer.IdIn(await apps.AppAsync(AppsServer.Her, "glucose"));
        var host = apps.Preview("glucose", id);
        var check = apps.CheckSessionFor(AppsServer.Her, host, "call-token", apps.Clock.GetUtcNow().AddSeconds(30));

        using var first = await apps.ReportAsync(host, Uncaught, check);
        using var second = await apps.ReportAsync(host, """{"kind":"error","message":"again","route":"/"}""", check);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await apps.GlobAsync(AppsServer.Her, "**/errors")).ShouldBeEmpty();
        metrics.Published.OfType<ErrorEvent>().ShouldBeEmpty();
    }

    // What a browser says is kept as it said it: quotes, markup and accents read back as written, never as
    // \u escapes the agent would have to decode.
    [Fact]
    public async Task AReportsText_IsKeptAsWritten()
    {
        var host = _apps.Published("glucose", _id);
        const string message = "TypeError: Cannot read properties of undefined (reading 'rows') <canvas> + glucemia baja";

        using var reported = await _apps.ReportAsync(host, new JsonObject { ["kind"] = "error", ["message"] = message, ["route"] = "/día" }.ToJsonString(),
            _apps.SessionFor(AppsServer.Her, host));

        reported.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var raw = await _apps.ReadAsync(AppsServer.Her, "/glucose/errors");
        raw.ShouldContain(message);
        raw.ShouldContain("/día");
    }

    // A shared app runs on every member's phone, and its author reads the errors file: a page could throw
    // what it read about its viewer (or post /_errors itself) and so hand it to the author past the reach an
    // owner approved. Another member's report keeps only what the server can check — the kind, a standard
    // error type, the stack's places in the app's own served files, a route that names a served file, a
    // read's operation, approved mount and code — and says it was reduced; no text the page wrote is kept,
    // in the file or in the operator's event. The author's own report keeps its text.
    [Fact]
    public async Task AnotherMembersReport_KeepsOnlyWhatTheServerChecks_AndNoTextThePageWrote()
    {
        var metrics = new Tests.Unit.RecordingMetricsPublisher();
        await using var apps = await AppsServer.StartAsync(services: services => services.AddSingleton<IMetricsPublisher>(metrics));
        var id = AppsServer.IdIn(await apps.AppAsync(AppsServer.Her, "glucose"));
        await apps.CreateAsync(AppsServer.Her, "/glucose/index.html", "<script type=\"module\" src=\"/main.js\"></script>");
        await apps.CreateAsync(AppsServer.Her, "/glucose/main.js", "throw new TypeError('rows is undefined');");
        await apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        apps.Spaces.Share(AppsServer.Her.TenantId, id, approved: new AppReach { Reads = ["/sugary"] });
        var host = apps.Published("glucose", id);
        var his = apps.SessionFor(AppsServer.Him, host);
        const string canary = "her-lentils-180";
        var bodies = new[]
        {
            new JsonObject
            {
                ["kind"] = "error", ["message"] = $"TypeError: {canary}",
                ["stack"] = $"TypeError: {canary}\n    at {canary} (https://{host}/main.js?{canary}:3:7)\n"
                            + $"    at https://{host}/{canary}.js:1:1\n    at https://cdn.jsdelivr.net/npm/{canary}@1.0.0/x.js:9:9\n"
                            + $"    at https://{host}/_app/apps.js:12:3\n    at https://{host}///main.js//:5:1",
                ["route"] = $"/{canary}"
            },
            new JsonObject { ["kind"] = "rejection", ["message"] = $"{canary}Error: {canary}", ["route"] = "/" },
            new JsonObject
            {
                ["kind"] = "read", ["message"] = canary, ["route"] = "///index.html//",
                ["read"] = new JsonObject
                {
                    ["operation"] = "query", ["path"] = $"/sugary/{canary}",
                    ["envelope"] = new JsonObject { ["errorCode"] = "permission_denied", ["message"] = canary, ["hint"] = canary }
                }
            },
            new JsonObject
            {
                ["kind"] = "read", ["message"] = canary, ["route"] = "/",
                ["read"] = new JsonObject
                {
                    ["operation"] = "read", ["path"] = $"/{canary}/a.md",
                    ["envelope"] = new JsonObject { ["errorCode"] = "permission_denied", ["message"] = canary }
                }
            }
        };

        foreach (var body in bodies)
        {
            using var reported = await apps.ReportAsync(host, body.ToJsonString(), his, userAgent: $"Mozilla/5.0 {canary}");
            reported.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reported.Content.ReadAsStringAsync());
        }

        (await apps.ReadAsync(AppsServer.Her, "/glucose/errors")).ShouldNotContain(canary);
        metrics.Published.OfType<ErrorEvent>().ShouldAllBe(e => !e.Message.Contains(canary));
        var entries = (await EntriesAsync(apps)).Reverse().ToList();
        entries.ShouldAllBe(e => e["viewer"]!.GetValue<string>() == AppsServer.Him.UserId
                                 && e["reduced"] != null && e["message"] == null && e["stack"] == null && e["userAgent"] == null);
        entries[0]["name"]!.GetValue<string>().ShouldBe("TypeError");
        // Kept in the server's own spelling: how the page wrote a path (its slashes) is the page's word too.
        entries[0]["frames"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldBe(["/main.js:3:7", "/_app/apps.js:12:3", "/main.js:5:1"]);
        entries[0]["route"].ShouldBeNull();
        entries[1]["kind"]!.GetValue<string>().ShouldBe("rejection");
        entries[1]["name"]!.GetValue<string>().ShouldBe("Error");
        entries[1]["route"]!.GetValue<string>().ShouldBe("/");
        entries[2]["route"]!.GetValue<string>().ShouldBe("/index.html");
        entries[2]["read"]!.ToJsonString().ShouldBe("""{"operation":"query","path":"/sugary","envelope":{"errorCode":"permission_denied","retryable":false}}""");
        entries[3]["read"]!["path"].ShouldBeNull();
        metrics.Published.OfType<ErrorEvent>().First().Message.ShouldBe($"glucose-{id}: error: TypeError");

        using var hers = await apps.ReportAsync(host, bodies[0].ToJsonString(), apps.SessionFor(AppsServer.Her, host),
            userAgent: $"Mozilla/5.0 {canary}");
        var own = (await EntriesAsync(apps))[0];
        own["reduced"].ShouldBeNull();
        own["message"]!.GetValue<string>().ShouldBe($"TypeError: {canary}");
        own["stack"]!.GetValue<string>().ShouldContain($"/{canary}.js:1:1");
        own["route"]!.GetValue<string>().ShouldBe($"/{canary}");
        metrics.Published.OfType<ErrorEvent>().Last().Message.ShouldContain(canary);
    }

    // The errors as the agent reads them: one JSON object a line, newest first.
    private Task<IReadOnlyList<JsonNode>> EntriesAsync() => EntriesAsync(_apps);

    private static async Task<IReadOnlyList<JsonNode>> EntriesAsync(AppsServer apps, string slug = "glucose")
    {
        var read = await apps.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = $"/{slug}/errors" },
            AppsServer.TurnOf(AppsServer.Her));
        var content = read["content"]?.GetValue<string>() ?? throw new InvalidOperationException(read.ToJsonString());
        return
        [
            .. content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..])!)
        ];
    }
}