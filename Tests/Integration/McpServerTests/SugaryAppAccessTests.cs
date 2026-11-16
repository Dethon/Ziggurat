using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using McpServerSugary.Channel;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The app's API believes the person the proxy's session check named in the assertion it signs and
// the proxy copies onto every request, and nothing else: not the three plain headers beside it, no
// code to type and no cookie of its own. A request without a believable assertion did not come
// through the check and is answered 401, which is what sends the app to sign in. /mcp keeps the
// deployment secret, and the headers do not open it.
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

    // The three plain headers are what any container on the compose network could write: without
    // the accounts service's signature over them, a request is nobody.
    [Fact]
    public async Task ARequestWithThePlainHeadersButNoAssertion_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var browser = server.Forwarded(SugaryServer.Her, assertion: null);

        (await browser.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Signed by a key the accounts service does not publish, signed for another app's route, the
    // household's guest, or out of date: none of them is a person signed in to Sugary.
    public static TheoryData<string, string> UnbelievedAssertions => new()
    {
        { "forged", TestAssertions.Forged(SugaryServer.Her, SugaryChannel.ChannelId) },
        { "for the chat hub", TestAssertions.Of(SugaryServer.Her, ChannelIds.SignalR) },
        { "the guest", TestAssertions.OfGuest(SugaryServer.Her.TenantId, SugaryChannel.ChannelId) },
        {
            "expired", TestAssertions.Of(SugaryServer.Her, SugaryChannel.ChannelId,
                now: DateTimeOffset.UtcNow.AddHours(-1), lifetime: TimeSpan.FromMinutes(10))
        }
    };

    [Theory]
    [MemberData(nameof(UnbelievedAssertions))]
    public async Task ARequestWhoseAssertionIsNotBelieved_IsRefused(string _, string assertion)
    {
        await using var server = await SugaryServer.StartAsync();
        var browser = server.Forwarded(SugaryServer.Her, assertion);

        (await browser.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Who the request is from is what was signed; plain headers naming someone else change nothing.
    [Fact]
    public async Task AnAssertionNamingHer_UnderHeadersNamingHim_ActsForHer()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        (await her.PostAsJsonAsync("/api/meals", Meal("her paella"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var spoofed = server.Forwarded(
            SugaryServer.Him, TestAssertions.Of(SugaryServer.Her, SugaryChannel.ChannelId));

        (await spoofed.GetFromJsonAsync<JsonArray>("/api/meals"))!.Single()!["notes"]!.GetValue<string>()
            .ShouldBe("her paella");
        (await server.As(SugaryServer.Him).GetFromJsonAsync<JsonArray>("/api/meals"))!.ShouldBeEmpty();
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

    private static JsonObject Meal(string notes) => new()
    {
        ["eatenAt"] = "2026-10-11T13:30:00+02:00",
        ["notes"] = notes,
        ["outcome"] = "unknown",
        ["items"] = new JsonArray(new JsonObject
        {
            ["name"] = "rice",
            ["grams"] = 150,
            ["carbsPer100g"] = 28,
            ["carbsLow"] = 32,
            ["carbs"] = 42,
            ["carbsHigh"] = 52,
            ["source"] = "estimate"
        })
    };

    [Fact]
    public async Task AnUnknownApiRoute_IsNotAnsweredWithTheApp()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);

        (await her.GetAsync("/api/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}