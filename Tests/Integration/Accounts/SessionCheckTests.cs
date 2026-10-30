using System.Net;
using Accounts.Members;
using Accounts.Sessions;
using Domain.Security;
using Domain.Security.Signing;
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

    // The fourth header vouches for the other three: signed by this service, it names the person,
    // their household and roles, and the channel the proxy asked for, which is the channel the turn
    // it starts must come on.
    [Theory]
    [InlineData("signalr")]
    [InlineData("sugary")]
    public async Task ASession_IsAnsweredWithAnAssertionOfItsPersonOnTheChannelAsked(string channel)
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await server.VerifyAsync(await server.MintSessionAsync(fran.UserId), channel: channel);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var assertion = await server.VerifyAssertionAsync(response.Headers.GetValues(ForwardedAccount.AssertionHeader).Single());
        assertion.IsVerified.ShouldBeTrue(assertion.Refusal.ToString());
        assertion.Claims.Subject.ShouldBe(fran.UserId);
        assertion.Claims.TenantId.ShouldBe(fran.TenantId);
        assertion.Claims.ChannelId.ShouldBe(channel);
        assertion.Claims.Roles.ShouldBe(["owner", "operator"]);
        assertion.Claims.IsGuest.ShouldBeFalse();
    }

    // The assertion also names the session it was checked for, which is what the chat hub refreshes
    // a connection's person by: while that session lives, and not after it is revoked.
    [Fact]
    public async Task ASessionsAssertion_NamesTheSessionItWasCheckedFor()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var phone = (await server.Service<SessionStore>().IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;

        var response = await server.VerifyAsync(phone.Token);

        var assertion = await server.VerifyAssertionAsync(response.Headers.GetValues(ForwardedAccount.AssertionHeader).Single());
        assertion.Claims.ShouldNotBeNull().SessionId.ShouldBe(phone.Id);
    }

    [Fact]
    public async Task AnAssertion_LivesTenMinutes()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var response = await server.VerifyAsync(await server.MintSessionAsync(fran.UserId));
        var assertion = response.Headers.GetValues(ForwardedAccount.AssertionHeader).Single();

        server.Clock.Advance(TimeSpan.FromMinutes(9));
        (await server.VerifyAssertionAsync(assertion)).IsVerified.ShouldBeTrue();
        server.Clock.Advance(TimeSpan.FromMinutes(2));
        (await server.VerifyAssertionAsync(assertion)).Refusal.ShouldBe(TokenRefusal.Expired);
    }

    // Every forward_auth names the channel or app it guards, so no route can be handed an assertion
    // naming no channel: a block that forgets is refused outright rather than half working.
    [Fact]
    public async Task ACheckNamingNoChannel_Is400()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await server.VerifyAsync(await server.MintSessionAsync(fran.UserId), channel: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Contains(ForwardedAccount.AssertionHeader).ShouldBeFalse();
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

    // "Sees all conversations" is named beside the role, so the chat hub learns it from the headers
    // it already reads and never asks the service again.
    [Fact]
    public async Task ASessionOfSomeoneWhoSeesAllConversations_NamesItBesideTheirRole()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        await server.Service<MemberStore>().SetSeesAllConversationsAsync(fran.TenantId, laura, true, CancellationToken.None);

        var response = await server.VerifyAsync(await server.MintSessionAsync(laura));

        response.Headers.GetValues("X-Ziggurat-Roles").ShouldBe([$"member,{ForwardedAccount.SeesAllRole}"]);
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
        response.Headers.Contains(ForwardedAccount.AssertionHeader).ShouldBeFalse();
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