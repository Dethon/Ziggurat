using System.Net;
using System.Net.Http.Json;
using Accounts.Tunnels;
using Domain.DTOs.Admin;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Security.Signing;
using Infrastructure.Tunnels;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.Tunnels;
using Tests.Unit;

namespace Tests.Integration.Accounts;

// The household's own servers in the admin area (seam 1 of Tenants 4c), reached as the chat client
// reaches them through the proxy, against the real relay: an owner adds a server and is shown its
// credential once, the server opens its tunnel with it and is listed connected with its tools, then
// offline once it stops; a credential revoked here closes its tunnel at once and opens nothing again.
// Only the caller's household's credentials are listed or revoked, and the credential is never in
// the change log or any later answer.
public class HouseholdServersAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";

    [Fact]
    public async Task AServerAddedByAnOwner_ConnectsWithTheCredentialShownOnce_AndIsListedWithItsTools()
    {
        await using var stack = await Stack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var admin = stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(ana));

        var minted = await AddAsync(admin, "vault");
        await using var server = await TunnelledServer.StartAsync(stack.Relay.Hub, minted.Credential, "vault");

        minted.ServerName.ShouldBe("vault");
        var listed = await ListedAsync(admin, ServerConnection.Connected);
        listed.Name.ShouldBe("vault");
        listed.IsOutpost.ShouldBeFalse();
        listed.ConnectedSince.ShouldBe(stack.Relay.Registry.Find(casa, "vault")!.OpenedAt);
        listed.Tools.ShouldBe(["wait", "who"]);
        listed.Credentials.ShouldBe([new AdminServerCredential(minted.CredentialId, stack.Accounts.Clock.GetUtcNow(), InUse: true)]);
        (await (await admin.GetAsync(AdminProtocol.Servers)).Content.ReadAsStringAsync()).ShouldNotContain(minted.Credential);
        var change = (await ChangesAsync(admin)).ShouldHaveSingleItem();
        change.Route.ShouldBe($"POST {AdminProtocol.Servers}");
        change.Summary.ShouldBe("Minted a tunnel credential for server vault");
    }

    [Fact]
    public async Task AServerThatStops_IsListedOffline_WithItsCredentialStillLive()
    {
        await using var stack = await Stack.StartAsync(redis);
        var (_, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var admin = stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(ana));
        var minted = await AddAsync(admin, "vault");
        var server = await TunnelledServer.StartAsync(stack.Relay.Hub, minted.Credential, "vault");
        await ListedAsync(admin, ServerConnection.Connected);

        await server.DisposeAsync();

        var offline = await ListedAsync(admin, ServerConnection.Offline);
        offline.ConnectedSince.ShouldBeNull();
        offline.Tools.ShouldBeNull();
        offline.Credentials.ShouldHaveSingleItem().ShouldBe(
            new AdminServerCredential(minted.CredentialId, stack.Accounts.Clock.GetUtcNow(), InUse: false));
    }

    [Fact]
    public async Task RevokingACredential_ClosesItsTunnelAtOnce_AndItCannotReconnect()
    {
        await using var stack = await Stack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var admin = stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(ana));
        var minted = await AddAsync(admin, "vault");
        await using var server = await TunnelledServer.StartAsync(stack.Relay.Hub, minted.Credential, "vault");
        await Eventually.Until(() => stack.Relay.Registry.Find(casa, "vault") is not null, "the tunnel opens");
        using var watching = new CancellationTokenSource();
        var closed = FirstClosedAsync(stack.Relay.Registry, watching.Token);

        var revoked = await admin.PostAsync(AdminProtocol.RevokeServerCredential(minted.CredentialId));

        revoked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await revoked.Content.ReadFromJsonAsync<AdminSaved<AdminServers>>())!;
        saved.Warning.ShouldBeNull();
        saved.Value.Servers.ShouldBeEmpty();
        (await closed.WaitAsync(TimeSpan.FromSeconds(10))).Reason.ShouldBe(TunnelProtocol.Revoked);
        await Eventually.Until(
            () => stack.Metrics.Published.OfType<ErrorEvent>().Any(e => e.Message.Contains(nameof(TunnelCredentialRefusal.Revoked))),
            "the server's next open is refused as revoked");
        stack.Relay.Registry.Find(casa, "vault").ShouldBeNull();
        (await ListAsync(admin)).Servers.ShouldBeEmpty();
        // Both made at the one instant the service's clock stands at, so in no particular order.
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(
            ["Minted a tunnel credential for server vault", "Revoked a tunnel credential of server vault"], ignoreOrder: true);
        await watching.CancelAsync();
    }

    // revoke-tunnel-credential names a credential by id alone; the admin route revokes one of the
    // caller's own household or none, so an id learnt elsewhere closes nobody else's tunnel.
    [Fact]
    public async Task AnotherHouseholdsCredential_IsNeitherListedNorRevoked()
    {
        await using var stack = await Stack.StartAsync(redis);
        var fran = await stack.Accounts.MintOperatorAsync("Fran");
        var (casa, _) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var theirs = await stack.Accounts.MintTunnelCredentialAsync(casa, "vault");
        await using var server = await TunnelledServer.StartAsync(stack.Relay.Hub, theirs.Credential, "vault");
        await Eventually.Until(() => stack.Relay.Registry.Find(casa, "vault") is not null, "their tunnel opens");
        var admin = stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(fran.UserId));

        var refused = await admin.PostAsync(AdminProtocol.RevokeServerCredential(theirs.Id));

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ListAsync(admin)).Servers.ShouldBeEmpty();
        (await stack.Accounts.Service<TunnelCredentialStore>().ListAsync(CancellationToken.None))
            .Single(c => c.Id == theirs.Id).RevokedAt.ShouldBeNull();
        await Eventually.Settle();
        stack.Relay.Registry.Find(casa, "vault").ShouldNotBeNull();
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANameThatIsNotAServerName_IsRefused_AndNothingIsMinted()
    {
        await using var stack = await Stack.StartAsync(redis);
        var (_, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var admin = stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(ana));

        var refused = await admin.PostAsync(AdminProtocol.Servers, new ServerAddition("My Vault"));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem().ShouldContain("lowercase letters");
        (await ListAsync(admin)).Servers.ShouldBeEmpty();
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    // A machine's outpost is a household server minted for `outpost-<NAME>`: owners mint those here
    // too, and the list tells it apart by its name as the relay does.
    [Fact]
    public async Task AnOutpost_IsAddedByItsServerName_AndListedAsOne()
    {
        await using var stack = await Stack.StartAsync(redis);
        var (_, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var admin = stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(ana));

        await AddAsync(admin, "outpost-laptop");
        await AddAsync(admin, "vault");

        var servers = (await ListAsync(admin)).Servers;
        servers.Select(s => (s.Name, s.IsOutpost, s.Connection)).ShouldBe(
            [("outpost-laptop", true, ServerConnection.Offline), ("vault", false, ServerConnection.Offline)]);
    }

    // The relay lives in the agent; an agent that cannot be asked leaves each server's state unknown,
    // never the list unanswered.
    [Fact]
    public async Task WithoutTheAgentsRelay_EachServerIsListed_AsUnknown()
    {
        var nowhere = TestPort.GetAvailable();
        try
        {
            await using var accounts = await AccountsServer.StartAsync(s => s with
            {
                Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
                AgentUrl = $"http://localhost:{nowhere}"
            });
            var (_, ana) = await accounts.SeedHouseholdAsync("Casa", "Ana");
            var admin = accounts.Admin(await accounts.MintSessionAsync(ana));

            await AddAsync(admin, "vault");

            (await ListAsync(admin)).Servers.ShouldHaveSingleItem().Connection.ShouldBe(ServerConnection.Unknown);
        }
        finally
        {
            TestPort.Release(nowhere);
        }
    }

    // With nobody to tell, the revocation stands and the relay refuses the credential on the tunnel's
    // next open; the owner is told the server may stay connected until then.
    [Fact]
    public async Task ARevocationTheRelayCouldNotBeTold_Stands_WithAWarning()
    {
        await using var accounts = await AccountsServer.StartAsync();
        var (_, ana) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        var admin = accounts.Admin(await accounts.MintSessionAsync(ana));
        var minted = await AddAsync(admin, "vault");

        var revoked = await admin.PostAsync(AdminProtocol.RevokeServerCredential(minted.CredentialId));

        revoked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await revoked.Content.ReadFromJsonAsync<AdminSaved<AdminServers>>())!.Warning
            .ShouldBe(global::Accounts.Admin.HouseholdServersAdmin.NotToldWarning);
        (await accounts.Service<TunnelCredentialStore>().ListAsync(CancellationToken.None))
            .Single(c => c.Id == minted.CredentialId).RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task NobodyButTheHouseholdsOwners_ManagesItsServers()
    {
        await using var accounts = await AccountsServer.StartAsync();
        var (casa, ana) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        var luis = await accounts.SeedPersonAsync(casa, "Luis");
        var minted = await AddAsync(accounts.Admin(await accounts.MintSessionAsync(ana)), "vault");

        foreach (var (admin, refusal) in new[]
                 {
                     (accounts.Admin(await accounts.MintSessionAsync(luis)), HttpStatusCode.Forbidden),
                     (accounts.Admin(session: null), HttpStatusCode.Unauthorized)
                 })
        {
            (await admin.GetAsync(AdminProtocol.Servers)).StatusCode.ShouldBe(refusal);
            (await admin.PostAsync(AdminProtocol.Servers, new ServerAddition("library"))).StatusCode.ShouldBe(refusal);
            (await admin.PostAsync(AdminProtocol.RevokeServerCredential(minted.CredentialId))).StatusCode.ShouldBe(refusal);
        }

        (await accounts.Service<TunnelCredentialStore>().ListAsync(CancellationToken.None))
            .ShouldHaveSingleItem().RevokedAt.ShouldBeNull();
    }

    private static async Task<MintedServerCredential> AddAsync(AccountsServer.AdminClient admin, string name)
    {
        var added = await admin.PostAsync(AdminProtocol.Servers, new ServerAddition(name));
        added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<AdminSaved<MintedServerCredential>>())!.Value;
    }

    private static async Task<AdminServers> ListAsync(AccountsServer.AdminClient admin)
    {
        var response = await admin.GetAsync(AdminProtocol.Servers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AdminServers>())!;
    }

    // The one server listed in that state, once the relay says so.
    private static async Task<AdminServer> ListedAsync(AccountsServer.AdminClient admin, ServerConnection state)
    {
        AdminServer? listed = null;
        await Eventually.Until(
            async () => (listed = (await ListAsync(admin)).Servers.SingleOrDefault(s => s.Connection == state)) is not null,
            $"the server is listed {state}");
        return listed!;
    }

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;

    private static async Task<TunnelClosed> FirstClosedAsync(TunnelRegistry registry, CancellationToken ct)
    {
        await foreach (var change in registry.WatchAsync(ct))
        {
            if (change is TunnelClosed closed)
            {
                return closed;
            }
        }

        throw new InvalidOperationException("the watch ended before any tunnel closed");
    }

    // The accounts service and the agent's relay, each asking the other as the deployment's do: the
    // relay verifies every tunnel's credential with accounts, and accounts lists a household's servers
    // through the relay's own listing. The relay's port is taken first, since each is told the other.
    private sealed class Stack(AccountsServer accounts, RelayHost relay, RecordingMetricsPublisher metrics, string keyDirectory)
        : IAsyncDisposable
    {
        public AccountsServer Accounts => accounts;

        public RelayHost Relay => relay;

        public RecordingMetricsPublisher Metrics => metrics;

        public static async Task<Stack> StartAsync(RedisFixture redis)
        {
            var port = TestPort.GetAvailable();
            var accounts = await AccountsServer.StartAsync(s => s with
            {
                Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
                Redis = new() { ConnectionString = redis.ConnectionString },
                AgentUrl = $"http://localhost:{port}"
            });
            var keyDirectory = Path.Combine(Path.GetTempPath(), "servers-admin-tests", Guid.NewGuid().ToString("N"));
            var keys = SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = keyDirectory });
            var metrics = new RecordingMetricsPublisher();
            var relay = await RelayHost.StartAsync(accounts, AgentCredential, redis.ConnectionString, keys, metrics, port);
            return new Stack(accounts, relay, metrics, keyDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            await relay.DisposeAsync();
            await accounts.DisposeAsync();
            Directory.Delete(keyDirectory, recursive: true);
        }
    }
}