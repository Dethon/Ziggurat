using System.Net;
using Accounts.Sessions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The session check at the boundary the proxy's forward_auth calls it on: what a request carrying
// a session, none, or the wrong one is answered, and the headers the proxy copies onto the request
// it passes to an app.
public class SessionCheckTests
{
    [Fact]
    public async Task ASession_IsAnsweredWithItsUserHouseholdAndRoles()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var session = await server.MintSessionAsync(fran.UserId);

        var response = await server.VerifyAsync(session);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Ziggurat-User").ShouldBe([fran.UserId]);
        response.Headers.GetValues("X-Ziggurat-Tenant").ShouldBe([fran.TenantId]);
        response.Headers.GetValues("X-Ziggurat-Roles").ShouldBe(["owner,operator"]);
    }

    [Fact]
    public async Task AMembersSession_NamesTheMemberRoleAlone()
    {
        await using var server = await AccountsServer.StartAsync();
        var laura = await server.SeedMemberAsync("Laura");

        var response = await server.VerifyAsync(await server.MintSessionAsync(laura));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Ziggurat-User").ShouldBe([laura]);
        response.Headers.GetValues("X-Ziggurat-Roles").ShouldBe(["member"]);
    }

    // A bookmark on any of the deployment's hosts still works: sign-in is on the main host, and it
    // is handed the whole address to come back to.
    [Fact]
    public async Task NoSession_SendsAPageRequestToSignInAndBack()
    {
        await using var server = await AccountsServer.StartAsync();

        var response = await server.VerifyAsync(null, uri: "/log?day=2026-10-29", host: "sugary.example");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldBe(new Uri(server.Address,
            "/accounts/sign-in?returnUrl=" + Uri.EscapeDataString("https://sugary.example/log?day=2026-10-29")));
    }

    // An app's script calling its API, its hub, or posting a form is answered plainly, so the page
    // can send the person to sign in rather than parse a sign-in page as data.
    [Theory]
    [InlineData("GET", "/api/meals", "application/json")]
    [InlineData("GET", "/api/attachments/abc", "text/html")]
    [InlineData("GET", "/hubs/chat/negotiate", "*/*")]
    [InlineData("POST", "/hubs/chat", "text/html")]
    [InlineData("POST", "/", "text/html")]
    [InlineData("GET", "/", "*/*")]
    public async Task NoSession_AnswersAnythingButAPage401(string method, string uri, string accept)
    {
        await using var server = await AccountsServer.StartAsync();

        var response = await server.VerifyAsync(null, method, uri, accept);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACookieTheStoreNeverIssued_IsNoSession()
    {
        await using var server = await AccountsServer.StartAsync();

        var response = await server.VerifyAsync("made-up", accept: "application/json");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains("X-Ziggurat-User").ShouldBeFalse();
    }

    [Fact]
    public async Task ARevokedSession_IsNoSession()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var sessions = server.Service<SessionStore>();
        var phone = (await sessions.IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var laptop = (await sessions.IssueAsync(fran.UserId, null, "laptop", CancellationToken.None))!;

        (await sessions.RevokeAsync(fran.UserId, phone.Id, CancellationToken.None)).ShouldBeTrue();

        (await server.VerifyAsync(phone.Token, accept: "application/json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.VerifyAsync(laptop.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The dashboard asks for the operator: a member is signed in, so is not sent to sign in again,
    // but is refused.
    [Fact]
    public async Task ARoleTheSessionLacks_IsForbidden()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");

        (await server.VerifyAsync(await server.MintSessionAsync(laura), role: "operator")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        var operatorCheck = await server.VerifyAsync(await server.MintSessionAsync(fran.UserId), role: "operator");
        operatorCheck.StatusCode.ShouldBe(HttpStatusCode.OK);
        operatorCheck.Headers.GetValues("X-Ziggurat-User").ShouldBe([fran.UserId]);
    }
}