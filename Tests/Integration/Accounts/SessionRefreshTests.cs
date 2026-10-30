using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Sessions;
using Domain.DTOs.Channel;
using Domain.Security;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The chat hub keeps a connection for hours on an assertion that lives ten minutes, so every turn it
// sends carries a fresh one — asked for the session the connection signed in with, never for an
// account by its id. A session answers while it lives; once its person revokes it, the next turn
// finds nobody to vouch for, which is what stops an open chat client that was signed out elsewhere.
public class SessionRefreshTests
{
    private const string SignalRCredential = "signalr-credential";

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["mcp-channel-signalr"] = SignalRCredential,
                ["mcp-channel-telegram"] = "telegram-credential",
                ["mcp-channel-voice"] = "voice-credential"
            }
        });

    [Fact]
    public async Task ALiveSession_IsAnsweredWithAFreshAssertionOfItsPersonOnTheChatHub()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var phone = await IssueAsync(server, fran.UserId);

        var response = await RefreshAsync(server, fran.UserId, phone.Id, SignalRCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        answer["userId"]!.GetValue<string>().ShouldBe(fran.UserId);
        answer["tenantId"]!.GetValue<string>().ShouldBe(fran.TenantId);
        answer["displayName"]!.GetValue<string>().ShouldBe("Fran");
        var assertion = await server.VerifyAssertionAsync(answer["assertion"]!.GetValue<string>());
        assertion.IsVerified.ShouldBeTrue(assertion.Refusal.ToString());
        assertion.Claims.Account.ShouldBe(new Account(fran.TenantId, fran.UserId));
        assertion.Claims.ChannelId.ShouldBe(ChannelIds.SignalR);
        assertion.Claims.SessionId.ShouldBe(phone.Id);
        assertion.Claims.Roles.ShouldBe(["owner", "operator"]);
    }

    [Fact]
    public async Task ARevokedSession_Is404()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var phone = await IssueAsync(server, fran.UserId);
        var laptop = await IssueAsync(server, fran.UserId);

        (await server.Service<SessionStore>().RevokeAsync(fran.UserId, phone.Id, CancellationToken.None)).ShouldBeTrue();

        (await RefreshAsync(server, fran.UserId, phone.Id, SignalRCredential)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RefreshAsync(server, fran.UserId, laptop.Id, SignalRCredential)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // A session's id is on its person's account page, so it is no secret: one named under somebody
    // else's account is nobody's, as a revoke naming it so is refused.
    [Fact]
    public async Task ASessionNamedUnderAnotherPerson_Is404()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var lauras = await IssueAsync(server, laura);

        (await RefreshAsync(server, fran.UserId, lauras.Id, SignalRCredential)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Only the chat hub keeps a connection over a session; Telegram's and voice's credentials vouch
    // for their own channels and cannot mint the chat hub's person.
    [Theory]
    [InlineData("telegram-credential")]
    [InlineData("voice-credential")]
    public async Task AnotherChannelsCredential_Is403(string credential)
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var phone = await IssueAsync(server, fran.UserId);

        (await RefreshAsync(server, fran.UserId, phone.Id, credential)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ACallerWithoutAConfiguredCredential_Is401()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var phone = await IssueAsync(server, fran.UserId);

        (await RefreshAsync(server, fran.UserId, phone.Id, "not-a-credential")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<IssuedSession> IssueAsync(AccountsServer server, string userId) =>
        (await server.Service<SessionStore>().IssueAsync(userId, null, "phone", CancellationToken.None)).ShouldNotBeNull();

    private static Task<HttpResponseMessage> RefreshAsync(AccountsServer server, string userId, string sessionId, string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/accounts/internal/session")
        {
            Content = JsonContent.Create(new { userId, sessionId })
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return server.Client().SendAsync(request);
    }
}