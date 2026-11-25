using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A built app's versions, through the action files its directory holds, run on the mount itself so a
// household with no sandbox has them: publish commits the working tree and serves it, history lists
// the commits with the served one marked, rollback serves an earlier one without rewriting anything.
public class AppsPublishingTests
{
    [Fact]
    public async Task Publish_CommitsTheWorkingTree_AndAnswersTheAppsLink()
    {
        await using var server = await AppsServer.StartAsync();
        var note = await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Hi</h1>");

        var published = await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        published["exitCode"]!.GetValue<int>().ShouldBe(0, published.ToJsonString());
        var link = Regex.Match(note, @"https://glucose-[a-z0-9]{6}\.apps\.test/").Value;
        Stdout(published).ShouldContain($"the app is served at {link}");
        var history = Lines(await server.ExecAsync(AppsServer.Her, "/glucose", "./history"));
        history.ShouldHaveSingleItem().ShouldStartWith("* ");
        history[0].ShouldEndWith("(served)");
    }

    // The bare name a listing shows runs the same action as ./name.
    [Fact]
    public async Task PublishingAgainUnchanged_CommitsNothing_AndKeepsServingTheSameCommit()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        var again = await server.ExecAsync(AppsServer.Her, "/glucose/publish", "publish");

        Stdout(again).ShouldStartWith("nothing changed since ");
        Lines(await server.ExecAsync(AppsServer.Her, "/glucose", "./history")).Count.ShouldBe(1);
    }

    // An npm project's .gitignore names its build output, which is exactly what the app serves.
    [Fact]
    public async Task Publish_CommitsWhatTheTreesOwnGitignoreNames()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/.gitignore", "dist/\n");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await server.CreateAsync(AppsServer.Her, "/glucose/dist/index.html", "<h1>Built</h1>");

        var published = await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        Stdout(published).ShouldStartWith("published ", customMessage: published.ToJsonString());
    }

    [Fact]
    public async Task Rollback_ServesAnEarlierCommit_WithoutRewritingHistory()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>One</h1>");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Two</h1>", overwrite: true);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        var first = Lines(await server.ExecAsync(AppsServer.Her, "/glucose", "./history"))[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var rolledBack = await server.ExecAsync(AppsServer.Her, "/glucose", $"./rollback {first}");

        rolledBack["exitCode"]!.GetValue<int>().ShouldBe(0, rolledBack.ToJsonString());
        var history = Lines(await server.ExecAsync(AppsServer.Her, "/glucose", "./history"));
        history.Count.ShouldBe(2);
        history[0].ShouldStartWith("  ");
        history[1].ShouldStartWith($"* {first}");
        (await server.ReadAsync(AppsServer.Her, "/glucose/index.html")).ShouldContain("Two");
        Lines(await server.ExecAsync(AppsServer.Her, "/glucose", "./history --limit 1")).ShouldHaveSingleItem();
    }

    // A revision is the model's text: only a commit of this app is served, and nothing it writes
    // reaches git as an option.
    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("deadbeef")]
    [InlineData("main@{1}")]
    public async Task ARevisionThatIsNoCommitOfTheApp_IsRefused(string revision)
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        var refused = await server.ExecAsync(AppsServer.Her, "/glucose", $"./rollback {revision}");

        refused["exitCode"]!.GetValue<int>().ShouldBe(1, refused.ToJsonString());
        refused["stderr"]!.GetValue<string>().ShouldContain("./history lists them");
    }

    // Outside code is pinned by version and hash in every page the app serves, nested ones too, and
    // nothing is committed or served while one is not. A page outside the served root is the agent's
    // source, never loaded by a person, so it is not held to it.
    [Fact]
    public async Task Publish_RefusesAServedPageLoadingUnpinnedOutsideCode_NamingEach()
    {
        await using var server = await AppsServer.StartAsync();
        await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose","accentColor":"#e9601f","description":"Mine.","root":"dist"}""");
        await server.CreateAsync(AppsServer.Her, "/glucose/dist/index.html",
            """<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js"></script>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/dist/week/index.html",
            """<script type="importmap">{"imports":{"preact":"https://esm.sh/preact@10.19.3"}}</script>""");
        await server.CreateAsync(AppsServer.Her, "/glucose/src/index.html", """<script src="https://esm.sh/anything"></script>""");

        var refused = await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        refused["exitCode"]!.GetValue<int>().ShouldBe(1, refused.ToJsonString());
        var stderr = refused["stderr"]!.GetValue<string>();
        stderr.ShouldStartWith("nothing published");
        stderr.ShouldContain("index.html: the script https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js has no integrity");
        stderr.ShouldContain("week/index.html: the import map names the outside module https://esm.sh/preact@10.19.3");
        stderr.ShouldNotContain("esm.sh/anything");
        Stdout(await server.ExecAsync(AppsServer.Her, "/glucose", "./history")).ShouldStartWith("no commits yet");
    }

    // The icon a manifest names is an SVG the published origin serves: one missing from the served root,
    // or not an SVG at all, refuses the publish naming it, and nothing is committed.
    [Theory]
    [InlineData(null, "is not a file the app serves")]
    [InlineData("<html><body>not a drawing</body></html>", "is not an SVG drawing")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"", "is not an SVG drawing")]
    public async Task Publish_RefusesAnIconThatIsNoSvgItServes(string? content, string words)
    {
        await using var server = await AppsServer.StartAsync();
        await server.CreateAsync(AppsServer.Her, "/casa/app.json",
            """{"name":"Casa","accentColor":"#c2410c","description":"Mine.","root":"dist","icon":"/icon.svg"}""");
        await server.CreateAsync(AppsServer.Her, "/casa/dist/index.html", "<h1>Casa</h1>");
        // Outside the served root it is the agent's source, never served.
        await server.CreateAsync(AppsServer.Her, "/casa/icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        if (content is not null)
        {
            await server.CreateAsync(AppsServer.Her, "/casa/dist/icon.svg", content);
        }

        var refused = await server.ExecAsync(AppsServer.Her, "/casa", "./publish");

        refused["exitCode"]!.GetValue<int>().ShouldBe(1, refused.ToJsonString());
        refused["stderr"]!.GetValue<string>().ShouldStartWith("nothing published");
        refused["stderr"]!.GetValue<string>().ShouldContain($"the icon /icon.svg {words}");
        Stdout(await server.ExecAsync(AppsServer.Her, "/casa", "./history")).ShouldStartWith("no commits yet");
    }

    [Fact]
    public async Task Publish_ServesTheIconsDrawingFromTheServedCommit()
    {
        await using var server = await AppsServer.StartAsync();
        const string drawing = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"><rect width=\"8\" height=\"8\"/></svg>";
        var created = await server.CreateAsync(AppsServer.Her, "/casa/app.json",
            """{"name":"Casa","accentColor":"#c2410c","description":"Mine.","icon":"/icon.svg"}""");
        var id = AppsServer.IdIn(created["note"]!.GetValue<string>());
        await server.CreateAsync(AppsServer.Her, "/casa/index.html", "<h1>Casa</h1>");
        await server.CreateAsync(AppsServer.Her, "/casa/icon.svg", drawing);

        var published = await server.ExecAsync(AppsServer.Her, "/casa", "./publish");
        await server.CreateAsync(AppsServer.Her, "/casa/icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", overwrite: true);

        published["exitCode"]!.GetValue<int>().ShouldBe(0, published.ToJsonString());
        var host = server.Published("casa", id);
        var icon = await server.GetAsync(host, "/_app/icon.svg", server.SessionFor(AppsServer.Her, host));
        (await icon.Content.ReadAsStringAsync()).ShouldBe(SpaceIcon.Badged(Encoding.UTF8.GetBytes(drawing)));
    }

    [Fact]
    public async Task AnythingElse_IsNotFound_NamingTheActions()
    {
        await using var server = await AppsServer.StartAsync();
        await server.AppAsync(AppsServer.Her, "glucose");

        var refused = await server.ExecAsync(AppsServer.Her, "/glucose", "npm run build");

        refused["exitCode"]!.GetValue<int>().ShouldBe(127);
        refused["stderr"]!.GetValue<string>().ShouldContain("available: publish, history, rollback");
    }

    private static string Stdout(JsonNode exec) => exec["stdout"]!.GetValue<string>();

    private static IReadOnlyList<string> Lines(JsonNode exec) =>
        Stdout(exec).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}