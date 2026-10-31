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

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(TestAssertions.Verifier());
        var app = builder.Build();
        app.UseOperatorGate();
        app.MapGet("/", () => "dashboard");
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