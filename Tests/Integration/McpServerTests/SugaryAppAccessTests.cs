using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Security;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The app's API believes the person the proxy's session check named, in the three headers the proxy
// copies onto every request, and nothing else: there is no code to type and no cookie of its own. A
// request without them did not come through the check and is answered 401, which is what sends the
// app to sign in. /mcp keeps the deployment secret, and the headers do not open it.
public class SugaryAppAccessTests
{
    public static TheoryData<string, string> ApiRoutes => new()
    {
        { "GET", "/api/session" },
        { "GET", "/api/meals" },
        { "POST", "/api/meals" },
        { "PUT", $"/api/meals/{Guid.NewGuid()}" },
        { "DELETE", $"/api/meals/{Guid.NewGuid()}" },
        { "GET", $"/api/meals/{Guid.NewGuid()}/photo" },
        { "GET", "/api/doses" },
        { "POST", "/api/doses" },
        { "PUT", $"/api/doses/{Guid.NewGuid()}" },
        { "DELETE", $"/api/doses/{Guid.NewGuid()}" },
        { "GET", "/api/pens" },
        { "POST", "/api/pens" },
        { "GET", "/api/push" },
        { "PUT", "/api/push/subscription" },
        { "DELETE", "/api/push/subscription?endpoint=https%3A%2F%2Fpush.example%2Fx" },
        { "GET", "/api/exercise" },
        { "POST", "/api/exercise" },
        { "GET", "/api/exercise/names" },
        { "PUT", $"/api/exercise/{Guid.NewGuid()}" },
        { "DELETE", $"/api/exercise/{Guid.NewGuid()}" },
        { "GET", "/api/periods" },
        { "POST", "/api/periods" },
        { "PUT", $"/api/periods/{Guid.NewGuid()}" },
        { "DELETE", $"/api/periods/{Guid.NewGuid()}" },
        { "GET", "/api/day-notes" },
        { "PUT", "/api/day-notes/2026-10-12" },
        { "DELETE", "/api/day-notes/2026-10-12" },
        { "GET", "/api/foods" },
        { "POST", "/api/foods" },
        { "PUT", $"/api/foods/{Guid.NewGuid()}" },
        { "DELETE", $"/api/foods/{Guid.NewGuid()}" },
        { "GET", "/api/recipes" },
        { "POST", "/api/recipes" },
        { "GET", "/api/products/8410188012092" },
        { "GET", "/api/export/meals.csv" },
        { "GET", "/api/export/items.csv" },
        { "GET", "/api/export/foods.csv" },
        { "GET", "/api/export/doses.csv" },
        { "GET", "/api/export/dose-meals.csv" },
        { "GET", "/api/export/exercise.csv" },
        { "GET", "/api/export/periods.csv" },
        { "GET", "/api/export/day-notes.csv" },
        { "POST", "/api/estimates" },
        { "POST", "/api/labels" },
        { "GET", "/api/nope" }
    };

    [Theory]
    [MemberData(nameof(ApiRoutes))]
    public async Task ARequestWithoutTheProxysHeaders_IsRefusedByEveryApiRoute(string method, string path)
    {
        await using var server = await SugaryServer.StartAsync();

        var response = await server.Browser().SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The check always answers all three, so a request carrying fewer did not come through it.
    [Theory]
    [InlineData(ForwardedAccount.UserHeader)]
    [InlineData(ForwardedAccount.TenantHeader)]
    [InlineData(ForwardedAccount.RolesHeader)]
    public async Task ARequestMissingOneOfTheHeaders_IsRefused(string missing)
    {
        await using var server = await SugaryServer.StartAsync();
        var browser = server.As(SugaryServer.Her);
        browser.DefaultRequestHeaders.Remove(missing);

        (await browser.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ASignedInRequest_ReachesTheApi()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);

        (await her.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = (await her.GetFromJsonAsync<JsonNode>("/api/session"))!;
        session["estimates"]!.GetValue<bool>().ShouldBeTrue();
        session["labels"]!.GetValue<bool>().ShouldBeTrue();
    }

    // Signing in and out is the accounts service's, for every app at once; the app has neither.
    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task TheAppsOwnSignInAndSignOut_AreGone(string method)
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);

        var response = await her.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/session")
        {
            Content = method == "POST" ? JsonContent.Create(new { code = "tortilla" }) : null
        });

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task TheHeaders_DoNotOpenMcp()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);

        var response = await her.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUnknownApiRoute_IsNotAnsweredWithTheApp()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);

        (await her.GetAsync("/api/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}