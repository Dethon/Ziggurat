using System.Net;
using System.Net.Http.Json;
using Domain.Apps;
using Domain.DTOs.Channel;
using Domain.Security;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The household's published apps, as the accounts service's admin API asks them for the spaces page:
// on the apps server's MCP listener, believed on an assertion the accounts service signed for an owner
// on its own channel, the household the assertion's. Each app as its served commit names it, whoever of
// the household wrote it; nothing unpublished, nothing of another household.
public class AppsPublishedListingTests
{
    private static readonly HttpClient _http = new();

    [Fact]
    public async Task AnOwner_IsToldTheHouseholdsPublishedApps_AsTheirServedCommitsNameThem()
    {
        await using var server = await AppsServer.StartAsync();
        var glucose = await PublishedAsync(server, AppsServer.Him, "glucose", "Glucose today");
        await server.CreateAsync(AppsServer.Him, "/glucose/app.json",
            """{"name":"Renamed, not published","accentColor":"#000000","description":"x"}""", overwrite: true);
        await server.AppAsync(AppsServer.Her, "draft");
        await PublishedAsync(server, AppsServer.Neighbour, "theirs", "Theirs");

        var listing = await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.PublishedApps, ["owner"]));

        listing.StatusCode.ShouldBe(HttpStatusCode.OK);
        listing.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var app = (await listing.Content.ReadFromJsonAsync<PublishedAppList>())!.Apps.ShouldHaveSingleItem();
        (app with { Reads = [], Connect = [], Asks = [] }).ShouldBe(new PublishedApp(glucose, "glucose", "Glucose today", "#e9601f",
            "My glucose today.", AppsServer.Him.UserId, [], [], $"https://glucose-{glucose}.{AppsServer.AppsDomain}/"));
        app.Reads.ShouldBe(["/sugary"]);
        app.Connect.ShouldBeEmpty();
        app.Asks.ShouldBeEmpty();
    }

    // The servers its asks may use are listed beside what it reads, as its served commit names them, so an
    // owner sees them before approving them.
    [Fact]
    public async Task AnAppsListing_NamesTheServersItsAsksMayUse_AsItsServedCommitDoes()
    {
        await using var server = await AppsServer.StartAsync();
        var glucose = await PublishedAsync(server, AppsServer.Her, "glucose", "Glucose today");
        await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","asks":["sugary","vault"]}""", overwrite: true);
        await server.ExecAsync(AppsServer.Her, "/glucose", "./publish");
        await server.CreateAsync(AppsServer.Her, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","asks":["homeassistant"]}""", overwrite: true);

        var listing = await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.PublishedApps, ["owner"]));

        var app = (await listing.Content.ReadFromJsonAsync<PublishedAppList>())!.Apps.ShouldHaveSingleItem();
        app.Id.ShouldBe(glucose);
        app.Asks.ShouldBe(["sugary", "vault"]);
        app.Reach.ShouldBe(new AppReach { Asks = ["sugary", "vault"] });
    }

    // Only the accounts service's own word for an owner opens it: a member, an assertion for another
    // channel (a viewer's apps assertion, the hub's), a forged one or none at all is refused.
    [Fact]
    public async Task AnythingButAnOwnersAssertionForTheListing_IsRefused()
    {
        await using var server = await AppsServer.StartAsync();
        await PublishedAsync(server, AppsServer.Her, "glucose", "Glucose today");

        (await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.PublishedApps))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.Apps, ["owner"]))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, TestAssertions.Of(AppsServer.Her, ChannelIds.SignalR, ["owner"]))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, TestAssertions.Forged(AppsServer.Her, AppChannels.PublishedApps))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The pages listener is the proxy's: an app origin answers its own pages and nothing of this server's.
    [Fact]
    public async Task TheListing_IsNotOnThePagesListener()
    {
        await using var server = await AppsServer.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.PagesAddress + PublishedApps.Path);
        request.Headers.Host = $"glucose-a1b2c3.{AppsServer.AppsDomain}";
        request.Headers.Add(ForwardedAccount.AssertionHeader, TestAssertions.Of(AppsServer.Her, AppChannels.PublishedApps, ["owner"]));
        var response = await _http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<string> PublishedAsync(AppsServer server, Account author, string slug, string name)
    {
        var id = AppsServer.IdIn(await server.AppAsync(author, slug, name));
        await server.CreateAsync(author, $"/{slug}/index.html", "<h1>Hi</h1>");
        await server.ExecAsync(author, $"/{slug}", "./publish");
        return id;
    }

    private static async Task<HttpResponseMessage> ListAsync(AppsServer server, string? assertion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{server.Settings.Listeners.Mcp}{PublishedApps.Path}");
        if (assertion is not null)
        {
            request.Headers.Add(ForwardedAccount.AssertionHeader, assertion);
        }

        var response = await _http.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }
}