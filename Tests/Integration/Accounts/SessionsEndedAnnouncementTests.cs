using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Accounts.Sessions;
using Accounts.Store;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// Every way a person's sessions end is told on Redis, so the chat hub closes the connections those
// sessions held: a device signed out from another one, a passkey revoked with the sessions it began,
// the person disabled. Untold, a page whose session was revoked elsewhere stayed connected and was
// pushed its person's new private conversations until it next tried to send.
public class SessionsEndedAnnouncementTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string Account = "/accounts/account";

    private Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with { Redis = new() { ConnectionString = redis.ConnectionString } });

    [Fact]
    public async Task SigningOutAnotherDevice_IsTold()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var sessions = server.Service<SessionStore>();
        var phone = (await sessions.IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var laptop = (await sessions.IssueAsync(fran.UserId, null, "laptop", CancellationToken.None))!;
        var heard = await ListenAsync();

        (await server.SignedIn(phone.Token).PostAsync(Account, $"{Account}/sessions/{laptop.Id}/revoke"))
            .StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        await Eventually.Until(() => heard.Contains(new SessionsEnded(fran.TenantId, fran.UserId)), "the ending to be told");
    }

    [Fact]
    public async Task RevokingAPasskey_IsTold()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (keep, revoke) = (await SeedPasskeyAsync(server, fran.UserId), await SeedPasskeyAsync(server, fran.UserId));
        var phone = (await server.Service<SessionStore>().IssueAsync(fran.UserId, keep, "phone", CancellationToken.None))!;
        await server.Service<SessionStore>().IssueAsync(fran.UserId, revoke, "laptop", CancellationToken.None);
        var heard = await ListenAsync();

        (await server.SignedIn(phone.Token).PostAsync(Account, $"{Account}/passkeys/{revoke}/revoke"))
            .StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        await Eventually.Until(() => heard.Contains(new SessionsEnded(fran.TenantId, fran.UserId)), "the ending to be told");
    }

    [Fact]
    public async Task DisablingAMember_IsTold()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        await server.MintSessionAsync(laura);
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        var heard = await ListenAsync();

        (await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.DisableMember(laura))
        { Content = JsonContent.Create(new { }) })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Eventually.Until(() => heard.Contains(new SessionsEnded(fran.TenantId, laura)), "the ending to be told");
    }

    // A refused revocation ended nothing, so nothing is told.
    [Fact]
    public async Task RevokingAnotherPersonsDevice_TellsNothing()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var frans = (await server.Service<SessionStore>().IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var lauras = await server.MintSessionAsync(laura);
        var heard = await ListenAsync();

        (await server.SignedIn(lauras).PostAsync(Account, $"{Account}/sessions/{frans.Id}/revoke"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await Eventually.Settle();
        heard.ShouldNotContain(e => e.UserId == fran.UserId || e.UserId == laura);
    }

    private static async Task<string> SeedPasskeyAsync(AccountsServer server, string userId)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        var credential = new Credential
        {
            Id = Tokens.NewId(), TenantId = user.TenantId, UserId = userId, CredentialId = Tokens.NewId(),
            PublicKey = [1], CreatedAt = server.Clock.GetUtcNow()
        };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync();
        return credential.Id;
    }

    // Pub/sub ignores the leased database, so each test reads only the people it made.
    private async Task<ConcurrentBag<SessionsEnded>> ListenAsync()
    {
        var heard = new ConcurrentBag<SessionsEnded>();
        await redis.Connection.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal(SessionsEnded.EndedChannel),
            (_, message) => heard.Add(JsonSerializer.Deserialize<SessionsEnded>(message.ToString(), JsonSerializerOptions.Web)!));
        return heard;
    }
}