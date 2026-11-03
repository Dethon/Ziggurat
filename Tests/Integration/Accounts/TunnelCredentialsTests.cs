using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounts.Commands;
using Accounts.Store;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A household's own server proves which household's server it is with a credential the operator
// minted for it: the relay asks this service to verify it on every open and is answered the
// household and the server's name, or why not. Revoking is told to whoever listens on Redis, so the
// relay closes the tunnel the credential opened.
public class TunnelCredentialsTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";
    private const string VoiceCredential = "voice-credential";

    private Task<AccountsServer> StartAsync(bool withRedis = true) =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["agent"] = AgentCredential,
                ["mcp-channel-voice"] = VoiceCredential
            },
            Redis = new() { ConnectionString = withRedis ? redis.ConnectionString : "" }
        });

    [Fact]
    public async Task AMintedCredential_VerifiesToItsHouseholdAndServerName_AndSaysTheHomeIsTheHome()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");
        var response = await VerifyAsync(server, minted.Credential, "vault");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var verified = (await response.Content.ReadFromJsonAsync<TunnelCredentialVerified>())!;
        verified.ShouldBe(new TunnelCredentialVerified(minted.Id, fran.TenantId, "vault", Home: true));
    }

    [Fact]
    public async Task AnUnknownCredential_IsRefusedAsUnknown()
    {
        await using var server = await StartAsync();

        var response = await VerifyAsync(server, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "vault");

        (await RefusalAsync(response)).ShouldBe(TunnelCredentialRefusal.Unknown);
    }

    [Fact]
    public async Task ATamperedCredential_IsRefusedAsUnknown()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");
        var tampered = (minted.Credential[0] == 'A' ? 'B' : 'A') + minted.Credential[1..];

        var response = await VerifyAsync(server, tampered, "vault");

        (await RefusalAsync(response)).ShouldBe(TunnelCredentialRefusal.Unknown);
    }

    [Fact]
    public async Task ARevokedCredential_IsRefusedAsRevoked()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        var (exit, output) = await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", minted.Id);
        var response = await VerifyAsync(server, minted.Credential, "vault");

        exit.ShouldBe(0, output);
        (await RefusalAsync(response)).ShouldBe(TunnelCredentialRefusal.Revoked);
    }

    // The tunnel says which server it is, and a credential minted for another of the household's
    // servers does not make it that one.
    [Fact]
    public async Task ACredentialForAnotherServerName_IsRefusedAsAnotherServer()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        var response = await VerifyAsync(server, minted.Credential, "library");

        (await RefusalAsync(response)).ShouldBe(TunnelCredentialRefusal.AnotherServer);
    }

    // A credential is replaced without a gap: the new one is minted and the server restarted with it
    // before the old one is revoked.
    [Fact]
    public async Task RevokingOneOfAServersCredentials_LeavesItsOtherOpening()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var old = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");
        var replacement = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", old.Id);

        (await VerifyAsync(server, replacement.Credential, "vault")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefusalAsync(await VerifyAsync(server, old.Credential, "vault"))).ShouldBe(TunnelCredentialRefusal.Revoked);
    }

    // The store alone opens no tunnel: what it keeps is the credential's SHA-256, never the value.
    [Fact]
    public async Task TheStore_KeepsOnlyTheCredentialsHash()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        var row = await db.TunnelCredentials.SingleAsync();
        row.CredentialHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(minted.Credential))));
        new[] { row.Id, row.TenantId, row.ServerName, row.CredentialHash }.ShouldNotContain(minted.Credential);
        // The write-ahead log too, where SQLite keeps what it has not checkpointed yet.
        var files = await server.StoredFilesAsync();
        files.ShouldNotBeEmpty();
        foreach (var (file, bytes) in files)
        {
            bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(minted.Credential)).ShouldBe(-1, file);
        }
    }

    // Shown once: the list names every credential and whether it is live, and never a secret.
    [Fact]
    public async Task TheList_NamesEveryCredentialAndWhetherItIsLiveButNoSecret()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var vault = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");
        var library = await server.MintTunnelCredentialAsync(fran.TenantId, "library");
        await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", library.Id);

        var (exit, output) = await server.RunAsync(AccountsCommand.ListTunnelCredentials);

        exit.ShouldBe(0, output);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Single(l => l.Contains(vault.Id)).ShouldContain($"server vault of household {fran.TenantId}");
        lines.Single(l => l.Contains(vault.Id)).ShouldEndWith("live");
        lines.Single(l => l.Contains(library.Id)).ShouldContain("revoked");
        output.ShouldNotContain(vault.Credential);
        output.ShouldNotContain(library.Credential);
    }

    [Fact]
    public async Task AListWithNothingMinted_SaysSo()
    {
        await using var server = await StartAsync();

        var (exit, output) = await server.RunAsync(AccountsCommand.ListTunnelCredentials);

        exit.ShouldBe(0, output);
        output.ShouldContain("No tunnel credential");
    }

    [Theory]
    [InlineData("Vault")]
    [InlineData("-vault")]
    [InlineData("my vault")]
    [InlineData("vault.home")]
    [InlineData("")]
    public async Task AnUnreadableServerName_MintsNothing(string serverName)
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var (exit, output) = await server.RunAsync(
            AccountsCommand.MintTunnelCredential, "--household", fran.TenantId, "--server", serverName);

        exit.ShouldBe(2, output);
        output.ShouldContain("no credential was minted");
        (await server.RunAsync(AccountsCommand.ListTunnelCredentials)).Output.ShouldContain("No tunnel credential");
    }

    [Fact]
    public async Task AHouseholdThatDoesNotExist_IsMintedNothing()
    {
        await using var server = await StartAsync();

        var (exit, output) = await server.RunAsync(
            AccountsCommand.MintTunnelCredential, "--household", "0123456789abcdef0123456789abcdef", "--server", "vault");

        exit.ShouldBe(1, output);
        output.ShouldContain("there is no household");
    }

    [Fact]
    public async Task RevokingACredentialThatDoesNotExist_RevokesNothing()
    {
        await using var server = await StartAsync();

        var (exit, output) = await server.RunAsync(
            AccountsCommand.RevokeTunnelCredential, "--id", "0123456789abcdef0123456789abcdef");

        exit.ShouldBe(1, output);
        output.ShouldContain("nothing was revoked");
    }

    // The relay closes the tunnel a revoked credential opened when it hears of it, so the revocation
    // names the credential, its household and its server.
    [Fact]
    public async Task Revoking_TellsTheListenersWhichCredentialHouseholdAndServer()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");
        var heard = await ListenAsync();

        var (exit, output) = await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", minted.Id);

        exit.ShouldBe(0, output);
        output.ShouldContain("The relay was told");
        await Eventually.Until(() => heard.Any(r => r.CredentialId == minted.Id), "the revocation to be published");
        heard.Single(r => r.CredentialId == minted.Id).ShouldBe(new TunnelCredentialRevoked(minted.Id, fran.TenantId, "vault"));
    }

    // Running the command again is the retry when the listeners could not be told the first time.
    [Fact]
    public async Task RevokingAnAlreadyRevokedCredential_TellsTheListenersAgain()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");
        await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", minted.Id);
        var heard = await ListenAsync();

        var (exit, output) = await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", minted.Id);

        exit.ShouldBe(0, output);
        output.ShouldContain("revoked already");
        await Eventually.Until(() => heard.Any(r => r.CredentialId == minted.Id), "the revocation to be published again");
    }

    // The revocation stands when nobody could be told, and the command says the open tunnel is not
    // closed yet rather than letting the operator believe it is.
    [Fact]
    public async Task RevokingWithNoRedisToTell_RevokesAndSaysTheRelayWasNotTold()
    {
        await using var server = await StartAsync(withRedis: false);
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        var (exit, output) = await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", minted.Id);

        exit.ShouldBe(1, output);
        output.ShouldContain("The relay was not told");
        (await RefusalAsync(await VerifyAsync(server, minted.Credential, "vault"))).ShouldBe(TunnelCredentialRefusal.Revoked);
    }

    [Fact]
    public async Task VerifyingWithoutACallersCredential_Is401()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        var response = await VerifyAsync(server, minted.Credential, "vault", caller: "not-a-caller");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The relay is the agent's: a channel server's credential, which opens that channel's people,
    // opens no tunnel question.
    [Fact]
    public async Task VerifyingAsAChannelServer_Is403()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var minted = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        var response = await VerifyAsync(server, minted.Credential, "vault", caller: VoiceCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // The key commands ask through the agent who holds a server of a name: the home always, every
    // household with a live credential for it — a suspended one too, since its server keeps its
    // delegations — and nobody whose credentials are revoked or for another server.
    [Fact]
    public async Task TheHoldersOfAServer_AreTheHomeAndEveryHouseholdWithALiveCredentialForIt()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var casa = await server.SeedHouseholdAsync("Casa");
        var piso = await server.SeedHouseholdAsync("Piso");
        var cabana = await server.SeedHouseholdAsync("Cabaña");
        var otro = await server.SeedHouseholdAsync("Otro");
        await server.MintTunnelCredentialAsync(casa, "homeassistant");
        await server.MintTunnelCredentialAsync(piso, "homeassistant");
        var revoked = await server.MintTunnelCredentialAsync(cabana, "homeassistant");
        await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", revoked.Id);
        await server.MintTunnelCredentialAsync(otro, "vault");
        await using (var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync())
        {
            (await db.Tenants.SingleAsync(t => t.Id == piso)).SuspendedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await HoldersAsync(server, "homeassistant");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<TunnelServerHolder>>())!.ShouldBe(
        [
            new TunnelServerHolder(fran.TenantId, "Home", Home: true, Suspended: false),
            new TunnelServerHolder(casa, "Casa", Home: false, Suspended: false),
            new TunnelServerHolder(piso, "Piso", Home: false, Suspended: true)
        ]);
    }

    [Fact]
    public async Task AskingForTheHoldersAsAChannelServer_Is403()
    {
        await using var server = await StartAsync();

        var response = await HoldersAsync(server, "homeassistant", caller: VoiceCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // A household's server on its accounts back-call is shown here by its own tunnel credential: it
    // may ask about voice in its household, and about nothing the relay asks — so a server cannot
    // verify another's credential with its own.
    [Fact]
    public async Task VerifyingAsAHouseholdsServer_Is403()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var voice = await server.MintTunnelCredentialAsync(fran.TenantId, "voice");
        var vault = await server.MintTunnelCredentialAsync(fran.TenantId, "vault");

        var response = await VerifyAsync(server, vault.Credential, "vault", caller: voice.Credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AHouseholdsServer_AsksTheGuestOfItsOwnHousehold()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var (household, _) = await server.SeedHouseholdAsync("Ana's", "Ana");
        var voice = await server.MintTunnelCredentialAsync(household, "voice");

        var response = await InternalAsync(server, "accounts/internal/guest", new { channel = "voice" }, voice.Credential);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenantId").GetString().ShouldBe(household);
    }

    // Only the household's voice server speaks for its people, whatever the relay lets through: a
    // printer's, vault's or outpost's live credential asks nothing here.
    [Theory]
    [InlineData("vault", "accounts/internal/guest")]
    [InlineData("outpost-laptop", "accounts/internal/resolve")]
    public async Task AHouseholdsOtherServer_Is403(string serverName, string path)
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var other = await server.MintTunnelCredentialAsync(fran.TenantId, serverName);

        var response = await InternalAsync(
            server, path, new { channel = "voice", identity = fran.UserId }, other.Credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // Revoking a credential closes the tunnel it opened and its back-calls with it, and one that still
    // arrived is nobody's.
    [Fact]
    public async Task ARevokedCredentialsServer_AsksNothing()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var voice = await server.MintTunnelCredentialAsync(fran.TenantId, "voice");
        (await server.RunAsync(AccountsCommand.RevokeTunnelCredential, "--id", voice.Id)).Exit.ShouldBe(0);

        var response = await InternalAsync(server, "accounts/internal/guest", new { channel = "voice" }, voice.Credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static Task<HttpResponseMessage> InternalAsync(AccountsServer server, string path, object body, string caller)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(caller));
        return server.Client().SendAsync(request);
    }

    private async Task<ConcurrentBag<TunnelCredentialRevoked>> ListenAsync()
    {
        var heard = new ConcurrentBag<TunnelCredentialRevoked>();
        // Pub/sub ignores the leased database, so a test reads only the revocations it caused.
        await redis.Connection.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal(TunnelCredentialProtocol.RevokedChannel),
            (_, message) => heard.Add(JsonSerializer.Deserialize<TunnelCredentialRevoked>(message.ToString(), JsonSerializerOptions.Web)!));
        return heard;
    }

    private static async Task<TunnelCredentialRefusal> RefusalAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        return (await response.Content.ReadFromJsonAsync<TunnelCredentialRefused>())!.Reason;
    }

    private static Task<HttpResponseMessage> HoldersAsync(
        AccountsServer server, string serverName, string caller = AgentCredential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TunnelCredentialProtocol.HoldersPath)
        {
            Content = JsonContent.Create(new TunnelServerHoldersQuery(serverName))
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(caller));
        return server.Client().SendAsync(request);
    }

    private static Task<HttpResponseMessage> VerifyAsync(
        AccountsServer server, string credential, string serverName, string caller = AgentCredential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TunnelCredentialProtocol.VerifyPath)
        {
            Content = JsonContent.Create(new TunnelCredentialQuery(credential, serverName))
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(caller));
        return server.Client().SendAsync(request);
    }
}