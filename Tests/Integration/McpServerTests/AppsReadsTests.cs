using System.Net;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Tools;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A built app's page reads the household at /_vfs/* on its own origin. The apps server knows the viewer
// by the app session and the app by the Host, as for a page, and forwards the read to the agent with its
// own credential, the app (id, building agent, the origin's reads) and the viewer's current `apps`
// assertion — against the agent's real route, over Sugary in process.
public sealed class AppsReadsTests : IAsyncLifetime
{
    private static readonly HouseholdSugaryPeople _people = new(
    [
        new SugaryPerson(AppsServer.Her.UserId, "Her", ReadsEveryLog: false),
        new SugaryPerson(AppsServer.Him.UserId, "Him", ReadsEveryLog: false)
    ]);

    private static readonly JsonObject _notes = new() { ["path"] = "/sugary", ["sql"] = "select notes from meals" };

    private SugaryServer _sugary = null!;
    private AppReadsAgent _agent = null!;
    private AppsServer _apps = null!;
    private string _id = null!;

    public async Task InitializeAsync()
    {
        _sugary = await SugaryServer.StartAsync(sugaryPeople: _people);
        await SaveMealAsync(AppsServer.Her, "her lentils");
        await SaveMealAsync(AppsServer.Him, "his paella");
        var households = new AgentHouseholds();
        households.Add(AppsServer.Her.TenantId, new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [_sugary.Endpoint], EnabledFeatures = ["filesystem"],
            WhitelistPatterns = ["domain__filesystem__*"]
        });
        _agent = await AppReadsAgent.StartAsync(households, new SharedServers { Sugary = _sugary.Endpoint });
        _apps = await AppsServer.StartAsync(settings => settings with { AgentUrl = _agent.Address });
        _id = AppsServer.IdIn(await _apps.AppAsync(AppsServer.Her, "glucose"));
        await _apps.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Glucose</h1>");
        await _apps.ExecAsync(AppsServer.Her, "/glucose", "./publish");
    }

    public async Task DisposeAsync()
    {
        await _apps.DisposeAsync();
        await _agent.DisposeAsync();
        await _sugary.DisposeAsync();
    }

    [Fact]
    public async Task AReadOnTheAppsOrigin_IsTheAgentsAnswer_ForTheViewer()
    {
        var host = _apps.Published("glucose", _id);

        using var read = await _apps.ReadAsync(host, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Her, host));

        var body = JsonNode.Parse(await read.Content.ReadAsStringAsync())!;
        read.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body["rows"]!.AsArray().Select(row => row![0]!.GetValue<string>()).ShouldBe(["her lentils"]);
        read.Headers.CacheControl!.NoStore.ShouldBeTrue();
        read.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldContain("default-src 'none'");
    }

    // The app reads what the origin's own manifest says: the published origin its served commit's, so a
    // manifest being rewritten in the tree widens nothing until it is published; the preview the tree's.
    [Fact]
    public async Task TheReadsForwarded_AreTheOriginsOwnManifests()
    {
        await _apps.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","reads":["/notes"]}""",
            overwrite: true);
        var published = _apps.Published("glucose", _id);
        var preview = _apps.Preview("glucose", _id);

        using var served = await _apps.ReadAsync(published, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Her, published));
        served.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var tree = await _apps.ReadAsync(preview, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Her, preview));
        tree.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var refused = JsonNode.Parse(await tree.Content.ReadAsStringAsync())!;
        refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        refused["message"]!.GetValue<string>().ShouldContain("/notes");
    }

    // An app shared before it read the household asks, once published to read /sugary, for more than an owner
    // approved: every viewer of its published origin, its author too, is refused that read with the
    // permission envelope, which says an owner approves it; its preview, its author's alone, reads it. Once
    // an owner approves it, the household's members read it, each their own.
    [Fact]
    public async Task AReadASharedAppAsksBeyondWhatAnOwnerApproved_IsRefused_UntilTheyApproveIt()
    {
        var host = _apps.Published("glucose", _id);
        var preview = _apps.Preview("glucose", _id);
        _apps.Spaces.Share(AppsServer.Her.TenantId, _id, approved: AppReach.None);

        using var his = await _apps.ReadAsync(host, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Him, host));
        using var hers = await _apps.ReadAsync(host, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Her, host));
        using var tree = await _apps.ReadAsync(preview, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Her, preview));
        _apps.Spaces.Share(AppsServer.Her.TenantId, _id, approved: new AppReach { Reads = ["/sugary"] });
        using var approved = await _apps.ReadAsync(host, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Him, host));

        foreach (var refused in new[] { his, hers })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var envelope = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
            envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
            envelope["message"]!.GetValue<string>().ShouldContain("/sugary");
            envelope["hint"]!.GetValue<string>().ShouldContain("owner");
        }

        tree.StatusCode.ShouldBe(HttpStatusCode.OK);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await approved.Content.ReadAsStringAsync())!["rows"]!.AsArray()
            .Select(row => row![0]!.GetValue<string>()).ShouldBe(["his paella"]);
    }

    // Nobody signed in, and a signed-in person who may not open the app, are answered the one 404 a page
    // gets: nothing is forwarded for them.
    [Fact]
    public async Task AReadByNobodyOrByAViewerWhoMayNotOpenTheApp_IsTheOne404()
    {
        var host = _apps.Published("glucose", _id);
        var page = await _apps.GetAsync(host, "/missing.css", _apps.SessionFor(AppsServer.Him, host));

        using var signedOut = await _apps.ReadAsync(host, AppReads.Operations.Query, _notes, session: null);
        using var stranger = await _apps.ReadAsync(host, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Him, host));

        foreach (var answer in new[] { signedOut, stranger })
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await answer.Content.ReadAsStringAsync()).ShouldBe(await page.Content.ReadAsStringAsync());
        }
    }

    // Sibling apps share the apps domain, so another app's page could make the viewer's browser post a read
    // here with this origin's cookie: a read is taken only from a page of this very origin, and nothing is
    // forwarded for another's.
    [Fact]
    public async Task AReadFromAnotherOriginsPage_IsTheOne404()
    {
        var host = _apps.Published("glucose", _id);
        var page = await _apps.GetAsync(host, "/missing.css", _apps.SessionFor(AppsServer.Him, host));

        using var foreign = await _apps.ReadAsync(host, AppReads.Operations.Query, _notes, _apps.SessionFor(AppsServer.Her, host),
            origin: $"https://other-abc123.{AppsServer.AppsDomain}");

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync()).ShouldBe(await page.Content.ReadAsStringAsync());
    }

    // An agent that cannot be reached answers the unavailable envelope, which the client library hands the
    // app as an error worth trying again.
    [Fact]
    public async Task AnAgentThatCannotBeReached_AnswersTheUnavailableEnvelope()
    {
        await using var apps = await AppsServer.StartAsync(settings => settings with { AgentUrl = DeadEnd.Url("") });
        var id = AppsServer.IdIn(await apps.AppAsync(AppsServer.Her, "glucose"));
        var preview = apps.Preview("glucose", id);

        using var read = await apps.ReadAsync(preview, AppReads.Operations.Query, _notes, apps.SessionFor(AppsServer.Her, preview));

        read.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var refused = JsonNode.Parse(await read.Content.ReadAsStringAsync())!;
        refused["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.TransientDependency);
        refused["retryable"]!.GetValue<bool>().ShouldBeTrue();
    }

    private async Task SaveMealAsync(Account person, string notes)
    {
        await using var db = _sugary.Services.GetRequiredService<SugaryStore>().For(person);
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