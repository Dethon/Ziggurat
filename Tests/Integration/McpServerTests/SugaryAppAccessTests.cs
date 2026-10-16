using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The app's routes carry their own gate: the access code the standalone app asks for, typed once
// per phone and kept as a cookie. /mcp keeps the deployment secret and is not opened by the cookie.
public class SugaryAppAccessTests
{
    public static TheoryData<string, string> ApiRoutes => new()
    {
        { "GET", "/api/meals" },
        { "POST", "/api/meals" },
        { "PUT", $"/api/meals/{Guid.NewGuid()}" },
        { "DELETE", $"/api/meals/{Guid.NewGuid()}" },
        { "GET", $"/api/meals/{Guid.NewGuid()}/photo" },
        { "GET", "/api/doses" },
        { "POST", "/api/doses" },
        { "PUT", $"/api/doses/{Guid.NewGuid()}" },
        { "DELETE", $"/api/doses/{Guid.NewGuid()}" },
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
        { "GET", "/api/products/8410188012092" },
        { "GET", "/api/export/meals.csv" },
        { "GET", "/api/export/items.csv" },
        { "GET", "/api/export/foods.csv" },
        { "GET", "/api/export/doses.csv" },
        { "GET", "/api/export/periods.csv" },
        { "GET", "/api/export/day-notes.csv" },
        { "POST", "/api/estimates" },
        { "POST", "/api/labels" }
    };

    [Theory]
    [MemberData(nameof(ApiRoutes))]
    public async Task APhoneThatHasNotSignedIn_IsRefusedByEveryApiRoute(string method, string path)
    {
        await using var server = await SugaryServer.StartAsync();

        var response = await server.Browser().SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheApi_IsClosedUntilTheCodeIsTyped()
    {
        await using var server = await SugaryServer.StartAsync();
        var browser = server.Browser();

        (await browser.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.PostAsJsonAsync("/api/session", new { code = "wrong" })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetFromJsonAsync<JsonNode>("/api/session"))!["authenticated"]!.GetValue<bool>().ShouldBeFalse();

        (await browser.PostAsJsonAsync("/api/session", new { code = $" {SugaryServer.AccessCode} " })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await browser.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = (await browser.GetFromJsonAsync<JsonNode>("/api/session"))!;
        session["authenticated"]!.GetValue<bool>().ShouldBeTrue();
        session["required"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task SigningOut_ClosesTheApiAgain()
    {
        await using var server = await SugaryServer.StartAsync();
        var browser = await server.SignedInAsync();

        (await browser.DeleteAsync("/api/session")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await browser.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // As with every shared secret here, a code nobody set opens nothing: a forgotten line in .env
    // must not leave her health log readable by anyone on the network.
    [Fact]
    public async Task WithNoCodeSet_EverySignInIsRefused()
    {
        await using var server = await SugaryServer.StartAsync(accessCode: "");
        var browser = server.Browser();

        (await browser.GetFromJsonAsync<JsonNode>("/api/session"))!["required"]!.GetValue<bool>().ShouldBeTrue();
        (await browser.PostAsJsonAsync("/api/session", new { code = "" })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync("/api/meals")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // A deployment reached only through its VPN can switch the code off. Every route then opens with
    // no cookie, and the session tells the app not to ask, so she never sees the sign-in screen.
    [Theory]
    [MemberData(nameof(ApiRoutes))]
    public async Task WithTheCodeSwitchedOff_EveryApiRouteOpensWithoutSigningIn(string method, string path)
    {
        await using var server = await SugaryServer.StartAsync(
            configure: settings => settings with { SugaryApp = new() { RequireAccessCode = false } });

        var response = await server.Browser().SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null
        });

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithTheCodeSwitchedOff_TheSessionSaysNoneIsNeeded()
    {
        await using var server = await SugaryServer.StartAsync(
            configure: settings => settings with { SugaryApp = new() { RequireAccessCode = false } });

        var session = (await server.Browser().GetFromJsonAsync<JsonNode>("/api/session"))!;

        session["required"]!.GetValue<bool>().ShouldBeFalse();
        session["authenticated"]!.GetValue<bool>().ShouldBeTrue();
    }

    // Guessing the code is slowed per address: ten tries a minute, and then that address waits.
    // Anyone else reaching the port is no reason for her phone to be locked out with it.
    [Fact]
    public async Task GuessingTheCode_IsSlowedForTheGuesser_AndNotForHer()
    {
        await using var server = await SugaryServer.StartAsync();
        var guesser = server.Browser(IPAddress.Parse("127.0.0.2"));
        var her = server.Browser(IPAddress.Loopback);

        for (var guess = 0; guess < 10; guess++)
        {
            (await guesser.PostAsJsonAsync("/api/session", new { code = $"guess-{guess}" })).StatusCode
                .ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await guesser.PostAsJsonAsync("/api/session", new { code = SugaryServer.AccessCode })).StatusCode
            .ShouldBe(HttpStatusCode.TooManyRequests);
        (await her.PostAsJsonAsync("/api/session", new { code = SugaryServer.AccessCode })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task TheCookie_DoesNotOpenMcp()
    {
        await using var server = await SugaryServer.StartAsync();
        var browser = await server.SignedInAsync();

        var response = await browser.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnUnknownApiRoute_IsNotAnsweredWithTheApp()
    {
        await using var server = await SugaryServer.StartAsync();
        var browser = await server.SignedInAsync();

        (await browser.GetAsync("/api/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}