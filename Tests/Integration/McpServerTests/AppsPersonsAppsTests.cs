using System.Net;
using System.Net.Http.Json;
using Domain.Apps;
using Domain.DTOs.Channel;
using Domain.Security;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A person's apps, as the chat hub asks them for WebChat's space switcher: on the apps server's MCP
// listener, believed on an assertion the accounts service signed for that person on its own channel, the
// household the assertion's. Their own published apps, and the links of the household's apps the hub
// names as shared (the app spaces its spaces read found); nothing unpublished, nobody else's private
// app, nothing of another household.
public class AppsPersonsAppsTests
{
    private static readonly HttpClient _http = new();

    [Fact]
    public async Task APerson_IsToldTheirOwnPublishedApps_AndTheLinksOfTheSharedOnesNamed()
    {
        await using var server = await AppsServer.StartAsync();
        var mine = await PublishedAsync(server, AppsServer.Her, "glucose", "Glucose today");
        await server.AppAsync(AppsServer.Her, "draft");
        var shared = await PublishedAsync(server, AppsServer.Him, "pantry", "Pantry");
        await PublishedAsync(server, AppsServer.Him, "diary", "His diary");
        var theirs = await PublishedAsync(server, AppsServer.Neighbour, "theirs", "Theirs");

        var listing = await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.PersonsApps), shared, theirs, "zzzzzz");

        listing.StatusCode.ShouldBe(HttpStatusCode.OK);
        listing.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        (await listing.Content.ReadFromJsonAsync<PersonsAppList>())!.Apps.ShouldBe(
        [
            new PersonsApp(mine, "Glucose today", "#e9601f", $"https://glucose-{mine}.{AppsServer.AppsDomain}/", Own: true),
            new PersonsApp(shared, "Pantry", "#e9601f", $"https://pantry-{shared}.{AppsServer.AppsDomain}/", Own: false)
        ], ignoreOrder: true);
    }

    // An app of their own the household shares is still theirs, and listed once.
    [Fact]
    public async Task AnOwnAppNamedAsShared_IsListedOnce_AsTheirs()
    {
        await using var server = await AppsServer.StartAsync();
        var mine = await PublishedAsync(server, AppsServer.Her, "glucose", "Glucose today");

        var listing = await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.PersonsApps), mine);

        (await listing.Content.ReadFromJsonAsync<PersonsAppList>())!.Apps.ShouldHaveSingleItem().Own.ShouldBeTrue();
    }

    // Only the accounts service's word for a person on this channel opens it: an assertion for another
    // channel (a viewer's apps assertion, the hub's own, an owner's listing), a forged one or none is refused.
    [Fact]
    public async Task AnythingButAnAssertionForThisChannel_IsRefused()
    {
        await using var server = await AppsServer.StartAsync();
        await PublishedAsync(server, AppsServer.Her, "glucose", "Glucose today");

        (await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.Apps))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, TestAssertions.Of(AppsServer.Her, ChannelIds.SignalR))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, TestAssertions.Of(AppsServer.Her, AppChannels.PublishedApps, ["owner"]))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, TestAssertions.OfGuest(AppsServer.Her.TenantId, AppChannels.PersonsApps))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, TestAssertions.Forged(AppsServer.Her, AppChannels.PersonsApps))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(server, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The pages listener is the proxy's: an app origin answers its own pages and nothing of this server's.
    [Fact]
    public async Task TheListing_IsNotOnThePagesListener()
    {
        await using var server = await AppsServer.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.PagesAddress + PersonsApps.Path);
        request.Headers.Host = $"glucose-a1b2c3.{AppsServer.AppsDomain}";
        request.Headers.Add(ForwardedAccount.AssertionHeader, TestAssertions.Of(AppsServer.Her, AppChannels.PersonsApps));
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

    private static async Task<HttpResponseMessage> ListAsync(AppsServer server, string? assertion, params string[] shared)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://localhost:{server.Settings.Listeners.Mcp}{PersonsApps.PathFor(shared)}");
        if (assertion is not null)
        {
            request.Headers.Add(ForwardedAccount.AssertionHeader, assertion);
        }

        var response = await _http.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }
}