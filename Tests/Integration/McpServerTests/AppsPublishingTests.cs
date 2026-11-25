using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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