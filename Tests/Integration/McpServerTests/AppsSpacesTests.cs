using System.Net;
using Domain.Apps;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A published app the household shares as an app space opens for every member of its household, the
// spaces section's word (read on the viewer's own `apps` assertion) deciding it; its preview stays its
// author's, and nobody of another household opens it whatever their household's spaces name. Unshared,
// it is its author's alone again. Against a real accounts service: AppSpacesOverTheWireTests.
public class AppsSpacesTests
{
    private static async Task<(AppsServer Server, string Id)> PublishedAsync()
    {
        var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        await server.CreateAsync(AppsServer.Her, "/glucose/index.html", "<h1>Published</h1>");
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        return (server, id);
    }

    [Fact]
    public async Task ASharedApp_OpensForAnotherMember_PagesAndReportsAlike()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        server.Spaces.Share(AppsServer.Her.TenantId, id);
        var his = server.SessionFor(AppsServer.Him, host);

        var page = await server.GetAsync(host, "/", his, navigate: true);
        var report = await server.ReportAsync(host, """{"kind":"error","message":"TypeError: x is undefined","route":"/"}""", his);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<h1>Published</h1>");
        report.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // The working tree is what its author is still writing: shared or not, nobody else sees it.
    [Fact]
    public async Task ASharedAppsPreview_StaysItsAuthorsAlone()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var preview = server.Preview("glucose", id);
        server.Spaces.Share(AppsServer.Her.TenantId, id);

        (await server.GetAsync(preview, "/", server.SessionFor(AppsServer.Him, preview), navigate: true)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await server.GetAsync(preview, "/", server.SessionFor(AppsServer.Her, preview), navigate: true)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    // A household's spaces are its own word about its own apps: another household naming this app's id in
    // its spaces opens nothing, and neither does this household sharing it to a member of another.
    [Fact]
    public async Task AnotherHouseholdsMember_GetsThe404_WhateverEitherHouseholdShares()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        server.Spaces.Share(AppsServer.Her.TenantId, id);
        server.Spaces.Share(AppsServer.Neighbour.TenantId, id);
        var unknownHost = $"glucose-zzzzzz.{AppsServer.AppsDomain}";
        var unknown = await server.GetAsync(unknownHost, "/", server.SessionFor(AppsServer.Neighbour, unknownHost), navigate: true);

        var refused = await server.GetAsync(host, "/", server.SessionFor(AppsServer.Neighbour, host), navigate: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync()).ShouldBe(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnsharedApp_IsItsAuthorsAloneAgain()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        server.Spaces.Share(AppsServer.Her.TenantId, id);
        var his = server.SessionFor(AppsServer.Him, host);
        (await server.GetAsync(host, "/", his, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        server.Spaces.Unshare(AppsServer.Her.TenantId);

        (await server.GetAsync(host, "/", his, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await server.GetAsync(host, "/", server.SessionFor(AppsServer.Her, host), navigate: true)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    // Whether it is shared cannot be told just now: another member is told to try again, never shown the
    // app nor told it is not there; its author, who needs no word of the household's, opens it.
    [Fact]
    public async Task SpacesThatCannotBeRead_OpenNothingForAnotherMember_AndEverythingForTheAuthor()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        server.Spaces.Share(AppsServer.Her.TenantId, id);
        server.Spaces.Down = true;

        (await server.GetAsync(host, "/", server.SessionFor(AppsServer.Him, host), navigate: true)).StatusCode
            .ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await server.GetAsync(host, "/", server.SessionFor(AppsServer.Her, host), navigate: true)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    // A read of the household from a shared app's page is opened by the same rule as its pages: shared, it
    // goes on to the agent (none in this fixture, so the unavailable envelope); unshared, the one 404;
    // spaces that cannot be read, the unavailable envelope saying so, and nothing goes to the agent.
    [Fact]
    public async Task AReadFromASharedAppsPage_IsOpenedByTheSameRule()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        var his = server.SessionFor(AppsServer.Him, host);
        var arguments = new System.Text.Json.Nodes.JsonObject { ["filePath"] = "/sugary/meals.md" };

        var unshared = await server.ReadAsync(host, "read", arguments, his);
        server.Spaces.Share(AppsServer.Her.TenantId, id);
        var shared = await server.ReadAsync(host, "read", arguments, his);
        server.Spaces.Down = true;
        var unchecked_ = await server.ReadAsync(host, "read", arguments, his);

        unshared.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        shared.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await shared.Content.ReadAsStringAsync()).ShouldNotContain("shared with the reader");
        unchecked_.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await unchecked_.Content.ReadAsStringAsync()).ShouldContain("shared with the reader");
    }

    // A reader that runs out of time is an outage like any other: told to try again, never a fault.
    [Fact]
    public async Task SpacesThatTakeTooLong_AreUnchecked_NotAFault()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        server.Spaces.Share(AppsServer.Her.TenantId, id);
        server.Spaces.TimesOut = true;

        (await server.GetAsync(host, "/", server.SessionFor(AppsServer.Him, host), navigate: true)).StatusCode
            .ShouldBe(HttpStatusCode.ServiceUnavailable);
        var deleted = await server.CallAsync("fs_delete", new Dictionary<string, object?> { ["path"] = "/glucose" },
            AppsServer.TurnOf(AppsServer.Her));
        deleted["errorCode"]!.GetValue<string>().ShouldBe(global::Domain.Tools.ToolError.Codes.TransientDependency, deleted.ToJsonString());
    }

    // An owner approved what the app reached when sharing it; a later publish asking for more is granted, on
    // the published origin and for every viewer, its author too, only what was approved — until an owner
    // approves the rest. The preview, its author's alone, keeps the manifest whole.
    [Fact]
    public async Task APublishWideningASharedApp_IsGrantedOnlyWhatAnOwnerApproved()
    {
        var (server, id) = await PublishedAsync();
        await using var _ = server;
        var host = server.Published("glucose", id);
        var preview = server.Preview("glucose", id);
        server.Spaces.Share(AppsServer.Her.TenantId, id,
            approved: new AppReach { Reads = ["/sugary"], Connect = ["https://approved.example.com"] });
        await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","reads":["/sugary"],"connect":["https://approved.example.com","https://tracker.example.com"]}""",
            overwrite: true);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");

        var his = await server.GetAsync(host, "/", server.SessionFor(AppsServer.Him, host), navigate: true);
        var hers = await server.GetAsync(host, "/", server.SessionFor(AppsServer.Her, host), navigate: true);
        var tree = await server.GetAsync(preview, "/", server.SessionFor(AppsServer.Her, preview), navigate: true);
        server.Spaces.Share(AppsServer.Her.TenantId, id,
            approved: new AppReach { Reads = ["/sugary"], Connect = ["https://approved.example.com", "https://tracker.example.com"] });
        var approved = await server.GetAsync(host, "/", server.SessionFor(AppsServer.Him, host), navigate: true);

        foreach (var page in new[] { his, hers })
        {
            var policy = page.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem();
            Directive(policy, "connect-src").ShouldContain("https://approved.example.com");
            Directive(policy, "connect-src").ShouldNotContain("https://tracker.example.com");
            Directive(policy, "img-src").ShouldNotContain("https://tracker.example.com");
        }

        Directive(tree.Headers.GetValues("Content-Security-Policy").Single(), "connect-src").ShouldContain("https://tracker.example.com");
        Directive(approved.Headers.GetValues("Content-Security-Policy").Single(), "connect-src").ShouldContain("https://tracker.example.com");
    }

    private static string Directive(string policy, string name) =>
        policy.Split(';', StringSplitOptions.TrimEntries).Single(d => d.StartsWith(name + " ", StringComparison.Ordinal));
}