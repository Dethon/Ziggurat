using System.Net;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Security;
using Domain.Tools;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// A built app's reads, as the apps server forwards them to the agent: its own credential, the viewer's
// `apps` assertion, and the app (id, building agent, reads). The agent reads through the building agent's
// mounts as a turn of it would, signed as the viewer, against servers in process over the MCP wire.
public sealed class AppReadsApiTests
{
    private static readonly AppReadScope _glucoseApp = new() { Id = "abc123", Agent = "jonas", Reads = ["/sugary"] };

    // Her and Him share a household and keep their own logs.
    private static readonly HouseholdSugaryPeople _people = new(
    [
        new SugaryPerson(SugaryServer.Her.UserId, "Her", ReadsEveryLog: false),
        new SugaryPerson(SugaryServer.Him.UserId, "Him", ReadsEveryLog: false)
    ]);

    [Fact]
    public async Task TwoViewersOfOneApp_EachReadTheirOwnSugaryRows()
    {
        await using var sugary = await SugaryServer.StartAsync(sugaryPeople: _people);
        await SaveMealAsync(sugary, SugaryServer.Her, "her lentils");
        await SaveMealAsync(sugary, SugaryServer.Him, "his paella");
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(sugary.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households, new SharedServers { Sugary = sugary.Endpoint });

        (await NotesAsync(agent, SugaryServer.Her)).ShouldBe(["her lentils"]);
        (await NotesAsync(agent, SugaryServer.Him)).ShouldBe(["his paella"]);
    }

    // Each operation is the file tool's own, answering its result on virtual paths: what the model would
    // have been answered, so an app reads what its agent read while building it.
    [Fact]
    public async Task EachReadOperation_AnswersTheToolsResultOnVirtualPaths()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string>
        {
            ["garden/plan.md"] = "tomatoes\npeppers",
            ["photo.png"] = "not really a picture"
        });
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(notes.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households);
        var app = _glucoseApp with { Reads = ["/notes"] };
        var her = TestAssertions.Of(SugaryServer.Her, AppChannels.Apps);

        var read = await JsonAsync(agent.ReadAsync(AppReads.Operations.Read, app, new JsonObject { ["filePath"] = "/notes/garden/plan.md" }, her));
        read["filePath"]!.GetValue<string>().ShouldBe("/notes/garden/plan.md");
        read["content"]!.GetValue<string>().ShouldContain("peppers");

        var glob = await JsonAsync(agent.ReadAsync(AppReads.Operations.Glob, app, new JsonObject { ["basePath"] = "/notes", ["pattern"] = "**/*.md" }, her));
        glob["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ShouldBe(["/notes/garden/plan.md"]);

        var info = await JsonAsync(agent.ReadAsync(AppReads.Operations.Info, app, new JsonObject { ["path"] = "/notes/garden/plan.md" }, her));
        info["path"]!.GetValue<string>().ShouldBe("/notes/garden/plan.md");
        info["exists"]!.GetValue<bool>().ShouldBeTrue();

        var search = await JsonAsync(agent.ReadAsync(AppReads.Operations.Search, app, new JsonObject { ["query"] = "peppers", ["directoryPath"] = "/notes" }, her));
        search["results"]!.AsArray().Select(r => r!["file"]!.GetValue<string>()).ShouldBe(["/notes/garden/plan.md"]);

        using var blob = await agent.ReadAsync(AppReads.Operations.Blob, app, new JsonObject { ["path"] = "/notes/photo.png" }, her);
        blob.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await blob.Content.ReadAsStringAsync()).ShouldBe("not really a picture");
    }

    // An app reads only the mount points its manifest names: a path anywhere else, though the agent
    // mounts it, is refused with the permission envelope. The registry is shared by two apps of one agent
    // and viewer, and each is held to its own reads.
    [Fact]
    public async Task AReadOutsideTheManifestsReads_IsRefusedWithThePermissionEnvelope()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string> { ["a.md"] = "notes" });
        await using var vault = await TextFileServer.StartAsync("vault", new Dictionary<string, string> { ["a.md"] = "vault" });
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(notes.Endpoint, vault.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households);
        var her = TestAssertions.Of(SugaryServer.Her, AppChannels.Apps);
        var notesApp = _glucoseApp with { Reads = ["/notes"] };
        var vaultApp = _glucoseApp with { Id = "def456", Reads = ["/vault"] };

        (await JsonAsync(agent.ReadAsync(AppReads.Operations.Read, notesApp, new JsonObject { ["filePath"] = "/notes/a.md" }, her)))
            ["content"]!.GetValue<string>().ShouldContain("notes");
        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Read, notesApp, new JsonObject { ["filePath"] = "/vault/a.md" }, her),
            ToolError.Codes.PermissionDenied, "/notes");
        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Glob, notesApp, new JsonObject { ["basePath"] = "/", ["pattern"] = "*" }, her),
            ToolError.Codes.PermissionDenied, "/notes");
        (await JsonAsync(agent.ReadAsync(AppReads.Operations.Read, vaultApp, new JsonObject { ["filePath"] = "/vault/a.md" }, her)))
            ["content"]!.GetValue<string>().ShouldContain("vault");
        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Read, vaultApp, new JsonObject { ["filePath"] = "/notes/a.md" }, her),
            ToolError.Codes.PermissionDenied, "/vault");
    }

    // Nothing that changes anything is reachable: every other operation, a file tool's or none, is
    // refused with the permission envelope and the file is as it was.
    [Theory]
    [InlineData("text_create")]
    [InlineData("create")]
    [InlineData("edit")]
    [InlineData("remove")]
    [InlineData("move")]
    [InlineData("copy")]
    [InlineData("exec")]
    [InlineData("write")]
    public async Task AnOperationThatIsNoRead_IsRefusedWithThePermissionEnvelope(string operation)
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string> { ["a.md"] = "kept" });
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(notes.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households);

        await RefusedAsync(agent.ReadAsync(operation, _glucoseApp with { Reads = ["/notes"] }, new JsonObject
        {
            ["path"] = "/notes/a.md", ["filePath"] = "/notes/a.md", ["content"] = "changed", ["command"] = "rm a.md",
            ["sourcePath"] = "/notes/a.md", ["destinationPath"] = "/notes/b.md"
        }, TestAssertions.Of(SugaryServer.Her, AppChannels.Apps)), ToolError.Codes.PermissionDenied, operation);

        (await File.ReadAllTextAsync(Path.Combine(notes.Directory, "a.md"))).ShouldBe("kept");
        File.Exists(Path.Combine(notes.Directory, "b.md")).ShouldBeFalse();
    }

    // An app reads only what its building agent's own call would run unasked: its whitelist decides, as
    // the approval client would, and a file tool the agent is not offered at all is refused too.
    [Fact]
    public async Task AReadTheBuildingAgentWouldAskAbout_IsRefusedWithThePermissionEnvelope()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string> { ["a.md"] = "notes" });
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId,
            Jonas(notes.Endpoint) with { WhitelistPatterns = ["domain__filesystem__glob", "domain__filesystem__file_info"] },
            Jonas(notes.Endpoint) with
            {
                Id = "nabu",
                Name = "Nabu",
                EnabledFeatures = ["filesystem.glob"],
                WhitelistPatterns = ["domain__filesystem__*"]
            });
        await using var agent = await AppReadsAgent.StartAsync(households);
        var her = TestAssertions.Of(SugaryServer.Her, AppChannels.Apps);
        var app = _glucoseApp with { Reads = ["/notes"] };

        (await JsonAsync(agent.ReadAsync(AppReads.Operations.Glob, app, new JsonObject { ["basePath"] = "/notes", ["pattern"] = "*" }, her)))
            ["entries"]!.AsArray().Count.ShouldBe(1);
        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Read, app, new JsonObject { ["filePath"] = "/notes/a.md" }, her),
            ToolError.Codes.PermissionDenied, "file_read");
        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Blob, app, new JsonObject { ["path"] = "/notes/a.md" }, her),
            ToolError.Codes.PermissionDenied, "file_read");

        var nabu = app with { Agent = "nabu" };
        (await JsonAsync(agent.ReadAsync(AppReads.Operations.Glob, nabu, new JsonObject { ["basePath"] = "/notes", ["pattern"] = "*" }, her)))
            ["entries"]!.AsArray().Count.ShouldBe(1);
        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Info, nabu, new JsonObject { ["path"] = "/notes/a.md" }, her),
            ToolError.Codes.PermissionDenied, "file_info");
    }

    // The household turning a feature off withholds its server from the agent's reads as from its turns:
    // the mount is not there, as the tool would answer.
    [Fact]
    public async Task AWithheldFeature_LeavesItsMountOut_AsForATurn()
    {
        await using var sugary = await SugaryServer.StartAsync(sugaryPeople: _people);
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, [Jonas(sugary.Endpoint)], new FeaturesSection { Sugary = false });
        await using var agent = await AppReadsAgent.StartAsync(households, new SharedServers { Sugary = sugary.Endpoint });

        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Query, _glucoseApp,
                new JsonObject { ["path"] = "/sugary", ["sql"] = "select notes from meals" },
                TestAssertions.Of(SugaryServer.Her, AppChannels.Apps)),
            ToolError.Codes.NotFound, "/sugary");
    }

    // A household server the agent names, set up and not connected, is unavailable rather than missing:
    // the read is worth asking again once it is back. Its name is not its mount point's.
    [Fact]
    public async Task AnOfflineOwnServer_AnswersTheUnavailableEnvelope()
    {
        await using var vault = await TextFileServer.StartAsync("vault", new Dictionary<string, string> { ["a.md"] = "vault" });
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, [Jonas("kit-files")], null, ownServers: ["kit-files"]);
        var online = new Dictionary<string, string>();
        await using var agent = await AppReadsAgent.StartAsync(households, online: online);
        var app = _glucoseApp with { Reads = ["/vault"] };
        var her = TestAssertions.Of(SugaryServer.Her, AppChannels.Apps);

        var refused = await RefusedAsync(
            agent.ReadAsync(AppReads.Operations.Read, app, new JsonObject { ["filePath"] = "/vault/a.md" }, her),
            ToolError.Codes.TransientDependency, "kit-files");
        refused["retryable"]!.GetValue<bool>().ShouldBeTrue();

        online["kit-files"] = vault.Endpoint;
        (await JsonAsync(agent.ReadAsync(AppReads.Operations.Read, app, new JsonObject { ["filePath"] = "/vault/a.md" }, her)))
            ["content"]!.GetValue<string>().ShouldContain("vault");
    }

    // A file crosses whole, so a blob read has the bridge's ceiling: past it the read is refused, not
    // served slowly. The text read pages through the tool's own window instead.
    [Fact]
    public async Task ABlobPastTheBridgesCeiling_IsRefused()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string>
        {
            ["small.png"] = "0123456789",
            ["large.png"] = "0123456789A"
        });
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(notes.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households, maxFileBytes: 10);
        var app = _glucoseApp with { Reads = ["/notes"] };
        var her = TestAssertions.Of(SugaryServer.Her, AppChannels.Apps);

        using var small = await agent.ReadAsync(AppReads.Operations.Blob, app, new JsonObject { ["path"] = "/notes/small.png" }, her);
        (await small.Content.ReadAsStringAsync()).ShouldBe("0123456789");
        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Blob, app, new JsonObject { ["path"] = "/notes/large.png" }, her),
            ToolError.Codes.InvalidArgument, "10 bytes");
    }

    // An app reads through the agent that built it, and only while the household has that agent: one it
    // no longer has refuses every read, naming it — read cold, or after a read was cached — and nothing
    // of it is kept, so the agent added back reads again.
    [Fact]
    public async Task ABuildingAgentTheHouseholdNoLongerHas_RefusesEveryRead_NamingIt()
    {
        await using var notes = await TextFileServer.StartAsync("notes", new Dictionary<string, string> { ["a.md"] = "notes" });
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(notes.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households);
        var her = TestAssertions.Of(SugaryServer.Her, AppChannels.Apps);
        var app = _glucoseApp with { Reads = ["/notes"] };
        var read = new JsonObject { ["filePath"] = "/notes/a.md" };

        await RefusedAsync(agent.ReadAsync(AppReads.Operations.Read, app with { Agent = "nabu" }, read, her),
            ToolError.Codes.PermissionDenied, "nabu");

        await JsonAsync(agent.ReadAsync(AppReads.Operations.Read, app, read, her));
        households.Add(SugaryServer.Her.TenantId, Jonas(notes.Endpoint) with { Id = "nabu", Name = "Nabu" });
        foreach (var operation in AppReads.Operations.All)
        {
            await RefusedAsync(agent.ReadAsync(operation, app, new JsonObject
            {
                ["path"] = "/notes", ["filePath"] = "/notes/a.md", ["basePath"] = "/notes", ["pattern"] = "*", ["query"] = "x",
                ["sql"] = "select 1"
            }, her), ToolError.Codes.PermissionDenied, "jonas");
        }

        households.Add(SugaryServer.Her.TenantId, Jonas(notes.Endpoint));
        await JsonAsync(agent.ReadAsync(AppReads.Operations.Read, app, read, her));
    }

    // Neither half opens the route alone: the apps server's credential without a believable assertion,
    // or an assertion without the credential (another server's, or none), is refused before anything is
    // built or read.
    public static TheoryData<string, string?, string?> Unbelieved => new()
    {
        { "no credential", null, TestAssertions.Of(SugaryServer.Her, AppChannels.Apps) },
        { "another credential", "wrong-secret", TestAssertions.Of(SugaryServer.Her, AppChannels.Apps) },
        { "no assertion", McpTestSecret.Value, null },
        { "a forged assertion", McpTestSecret.Value, TestAssertions.Forged(SugaryServer.Her, AppChannels.Apps) },
        { "another channel's assertion", McpTestSecret.Value, TestAssertions.Of(SugaryServer.Her, AppChannels.SexyTime) },
        { "the guest's assertion", McpTestSecret.Value, TestAssertions.OfGuest(SugaryServer.Her.TenantId, AppChannels.Apps) },
        { "nobody's assertion", McpTestSecret.Value, TestAssertions.OfNobody(AppChannels.Apps) }
    };

    [Theory]
    [MemberData(nameof(Unbelieved))]
    public async Task ARequestWithoutBothTheCredentialAndABelievableAssertion_IsRefused(
        string what, string? credential, string? assertion)
    {
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas());
        await using var agent = await AppReadsAgent.StartAsync(households);

        using var response = await agent.ReadAsync(
            AppReads.Operations.Info, _glucoseApp, new JsonObject { ["path"] = "/sugary" }, assertion, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, what);
    }

    // Who reads is the assertion's person alone: the identity headers the proxy writes elsewhere,
    // naming somebody else, are never read.
    [Fact]
    public async Task TheIdentityHeadersBesideTheAssertion_AreNeverRead()
    {
        await using var sugary = await SugaryServer.StartAsync(sugaryPeople: _people);
        await SaveMealAsync(sugary, SugaryServer.Her, "her lentils");
        await SaveMealAsync(sugary, SugaryServer.Him, "his paella");
        var households = new AgentHouseholds();
        households.Add(SugaryServer.Her.TenantId, Jonas(sugary.Endpoint));
        await using var agent = await AppReadsAgent.StartAsync(households, new SharedServers { Sugary = sugary.Endpoint });

        (await NotesAsync(agent, SugaryServer.Her, new Dictionary<string, string>
        {
            [ForwardedAccount.UserHeader] = SugaryServer.Him.UserId,
            [ForwardedAccount.TenantHeader] = SugaryServer.Him.TenantId,
            [ForwardedAccount.RolesHeader] = "owner"
        })).ShouldBe(["her lentils"]);
    }

    private static async Task<IReadOnlyList<string?>> NotesAsync(
        AppReadsAgent agent, Account viewer, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = await agent.ReadAsync(
            AppReads.Operations.Query, _glucoseApp,
            new JsonObject { ["path"] = "/sugary", ["sql"] = "select notes from meals" },
            TestAssertions.Of(viewer, AppChannels.Apps), headers: headers);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return [.. body["rows"]!.AsArray().Select(row => row![0]?.GetValue<string>())];
    }

    // The tools' envelope, answered 422, its message naming what it should.
    private static async Task<JsonNode> RefusedAsync(Task<HttpResponseMessage> sent, string errorCode, string named)
    {
        using var response = await sent;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body.ToJsonString());
        body["ok"]!.GetValue<bool>().ShouldBeFalse();
        body["errorCode"]!.GetValue<string>().ShouldBe(errorCode, body.ToJsonString());
        body["message"]!.GetValue<string>().ShouldContain(named);
        return body;
    }

    private static async Task<JsonNode> JsonAsync(Task<HttpResponseMessage> sent)
    {
        using var response = await sent;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return body;
    }

    private static AgentDefinition Jonas(params string[] endpoints) => new()
    {
        Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = endpoints, EnabledFeatures = ["filesystem"],
        WhitelistPatterns = ["domain__filesystem__*"]
    };

    private static async Task SaveMealAsync(SugaryServer server, Account person, string notes)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        db.Meals.Add(new Meal
        {
            Id = Guid.NewGuid(),
            EatenAt = DateTimeOffset.Parse("2026-10-11T12:00:00Z"),
            Notes = notes,
            Outcome = MealOutcome.High,
            FirstEstimate = new Interval(30, 40, 55),
            Items = [],
            CarbsLow = 30,
            Carbs = 40,
            CarbsHigh = 55
        });
        await db.SaveChangesAsync();
    }
}