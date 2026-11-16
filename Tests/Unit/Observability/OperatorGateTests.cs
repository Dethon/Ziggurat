using System.Net;
using Domain.DTOs.Channel;
using Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Observability;
using Shouldly;

namespace Tests.Unit.Observability;

// Memory text and usage per person are the operator's alone. The proxy checks the operator's session
// before every request to the dashboard, and the dashboard holds the request to it again: it believes
// the person the accounts service's assertion names, for the dashboard's channel, and never the three
// plain headers beside it, which anything on the compose network could write. A request naming
// nobody believable is signed out; a believable person who is not the operator is refused.
public sealed class OperatorGateTests
{
    private const string Home = "fedcba9876543210fedcba9876543210";
    private static readonly Account _operator = new(Home, "0123456789abcdef0123456789abcdef");
    private static readonly Account _member = new(Home, "91eb477ecd34251c52fd4493523a0334");

    private static readonly string[] _operatorRoles = ["owner", ForwardedAccount.OperatorRole];

    [Fact]
    public async Task TheOperatorsAssertion_IsAnswered()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().SendAsync(
            Request(_operator, TestAssertions.Of(_operator, AppChannels.Dashboard, _operatorRoles)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("dashboard");
    }

    // The plain headers alone, naming the operator; the operator's claims signed by a key the accounts
    // service does not publish; and the operator signed for another app's route.
    public static TheoryData<string, string?> Unbelieved => new()
    {
        { "none", null },
        { "forged", TestAssertions.Forged(_operator, AppChannels.Dashboard) },
        { "for the chat hub", TestAssertions.Of(_operator, ChannelIds.SignalR, _operatorRoles) }
    };

    [Theory]
    [MemberData(nameof(Unbelieved))]
    public async Task ARequestWithoutABelievableAssertion_IsSignedOut(string _, string? assertion)
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().SendAsync(Request(_operator, assertion));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Who the request is from was signed: plain headers naming the operator do not make a member one.
    [Fact]
    public async Task AMembersAssertion_UnderHeadersNamingTheOperator_IsForbidden()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().SendAsync(
            Request(_operator, TestAssertions.Of(_member, AppChannels.Dashboard, ["member"])));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // An installed dashboard replaces its service worker by fetching the worker's script, and installs
    // the new one by fetching the client's files, signed out as often as not — the worker from before
    // sign-in answers every page with its cached app, so without them the operator's installed app was
    // stuck on it for good. The client's own files are served to such a request; a page being opened,
    // anything asking for HTML, the API, the hub and every path that is not a file of the client stay
    // the operator's.
    [Theory]
    [InlineData("/service-worker.js", null, null, HttpStatusCode.OK)]
    [InlineData("/_framework/blazor.webassembly.js", "same-origin", "*/*", HttpStatusCode.OK)]
    [InlineData("/service-worker.js", "navigate", null, HttpStatusCode.Unauthorized)]
    [InlineData("/index.html", null, "text/html,application/xhtml+xml", HttpStatusCode.Unauthorized)]
    [InlineData("/api/metrics/summary", null, null, HttpStatusCode.Unauthorized)]
    [InlineData("/hubs/metrics/negotiate", null, null, HttpStatusCode.Unauthorized)]
    [InlineData("/not-a-file.js", null, null, HttpStatusCode.Unauthorized)]
    public async Task TheClientsOwnFiles_AreServedSignedOut_AndNothingElse(
        string path, string? fetchMode, string? accept, HttpStatusCode expected)
    {
        await using var app = await StartAsync(withClientFiles: true);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (fetchMode is not null)
        {
            request.Headers.Add("Sec-Fetch-Mode", fetchMode);
        }

        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.ShouldBe(expected, path);
    }

    private static async Task<WebApplication> StartAsync(bool withClientFiles = false)
    {
        var webRoot = Directory.CreateTempSubdirectory("operator-gate-").FullName;
        foreach (var file in new[] { "service-worker.js", "index.html", "_framework/blazor.webassembly.js" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(webRoot, file))!);
            await File.WriteAllTextAsync(Path.Combine(webRoot, file), file);
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TestAssertions.Verifier());
        var app = builder.Build();
        app.UseOperatorGate();
        if (withClientFiles)
        {
            app.UseStaticFiles();
        }

        app.MapGet("/", () => "dashboard");
        app.MapGet("/api/metrics/summary", () => "summary");
        await app.StartAsync();
        return app;
    }

    // As the proxy hands a request on: the plain headers naming whoever a test likes, roles that say
    // operator, and the assertion it chose.
    private static HttpRequestMessage Request(Account plain, string? assertion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(ForwardedAccount.UserHeader, plain.UserId);
        request.Headers.Add(ForwardedAccount.TenantHeader, plain.TenantId);
        request.Headers.Add(ForwardedAccount.RolesHeader, string.Join(',', _operatorRoles));
        if (assertion is not null)
        {
            request.Headers.Add(ForwardedAccount.AssertionHeader, assertion);
        }

        return request;
    }
}