using Domain.Security;
using Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The agent and the hub both answer browsers with a visitor's credentials, so the origins they
// admit are the deployment's own and nothing else. Asserted on the headers, never the status: the
// CORS middleware answers every preflight 204, admitted or not, and a refusal is the absence of
// Access-Control-Allow-Origin.
public class CorsExtensionsTests
{
    private const string Ours = "https://assistants.example.com";
    private const string Foreign = "https://evil.example.com";

    [Fact]
    public async Task APreflightFromAForeignOrigin_GetsNoCorsHeaders()
    {
        await using var app = await StartAsync([Ours]);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Preflight(Foreign));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    [Fact]
    public async Task APreflightFromAListedOrigin_IsAdmittedWithCredentials()
    {
        await using var app = await StartAsync([Ours]);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Preflight(Ours));

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([Ours]);
        response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldBe(["true"]);
    }

    // A deployment that never named its origins is same-origin only, not open: the safe value is
    // the one an unset setting produces.
    [Theory]
    [InlineData(Ours)]
    [InlineData(Foreign)]
    public async Task WithNoOriginsListed_NoCrossOriginPreflightIsAdmitted(string origin)
    {
        await using var app = await StartAsync([]);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Preflight(origin));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    private static async Task<WebApplication> StartAsync(string[] allowedOrigins)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDeploymentCors(new CorsSettings { AllowedOrigins = allowedOrigins });

        var app = builder.Build();
        app.UseCors();
        app.MapPost("/api/thing", () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/thing");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }
}