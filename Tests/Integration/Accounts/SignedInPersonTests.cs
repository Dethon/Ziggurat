using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Sessions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// What an app's page asks to show the person who is signed in: same-origin through the proxy, the
// session cookie riding along, so the chat client names its person without being told by anyone
// but the accounts service.
public class SignedInPersonTests
{
    [Fact]
    public async Task ASession_IsAnsweredWithWhoItIs()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await MeAsync(server, await server.MintSessionAsync(fran.UserId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var me = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        me["userId"]!.GetValue<string>().ShouldBe(fran.UserId);
        me["tenantId"]!.GetValue<string>().ShouldBe(fran.TenantId);
        me["displayName"]!.GetValue<string>().ShouldBe("Fran");
        me["avatarUrl"].ShouldBeNull();
        me["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["owner", "operator"]);
    }

    [Fact]
    public async Task AMembersSession_IsThatMember()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");

        var me = (await (await MeAsync(server, await server.MintSessionAsync(laura))).Content.ReadFromJsonAsync<JsonObject>())!;

        me["userId"]!.GetValue<string>().ShouldBe(laura);
        me["displayName"]!.GetValue<string>().ShouldBe("Laura");
        me["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["member"]);
    }

    // Answered plainly, never sent to sign-in: the page asking is a script, and it is the one that
    // takes the person to sign in.
    [Theory]
    [InlineData(null)]
    [InlineData("made-up")]
    public async Task NoSession_Is401(string? session)
    {
        await using var server = await AccountsServer.StartAsync();

        var response = await MeAsync(server, session);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static Task<HttpResponseMessage> MeAsync(AccountsServer server, string? session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/accounts/me");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        }

        return server.Client().SendAsync(request);
    }
}