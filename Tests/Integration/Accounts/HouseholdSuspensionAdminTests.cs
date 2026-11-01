using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The operator suspends a household from the list and resumes it later (seam 1 of Tenants 4c): while
// suspended its sessions are refused at the session check (SessionCheckTests), its tunnel credentials
// open nothing, the agent's view of it says so — which is how its turns and fires are refused
// (ChatMonitorAssertionTests, ChatMonitorDelegationTests) — and the relay is told to close its open
// tunnels (TunnelRelayTests). Nothing is deleted, so resuming restores all of it.
public class HouseholdSuspensionAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";

    private Task<AccountsServer> StartAsync(bool withRedis = true) =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = withRedis ? redis.ConnectionString : "" }
        });

    [Fact]
    public async Task TheOperator_SuspendsAHousehold_AndTheListAndItsChangesSaySo()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var suspended = await SuspendAsync(admin, casa, true);

        suspended.StatusCode.ShouldBe(HttpStatusCode.OK);
        suspended.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var saved = (await suspended.Content.ReadFromJsonAsync<AdminSaved<HouseholdSuspension>>())!;
        saved.Value.ShouldBe(new HouseholdSuspension(true, server.Clock.GetUtcNow()));
        saved.Warning.ShouldBeNull();
        var households = await ListAsync(admin);
        households.Single(h => h.Id == casa).SuspendedAt.ShouldBe(server.Clock.GetUtcNow());
        households.Single(h => h.IsHome).SuspendedAt.ShouldBeNull();
        var change = (await ChangesAsync(admin, casa)).ShouldHaveSingleItem();
        change.ActorUserId.ShouldBe(fran.UserId);
        change.Route.ShouldBe($"PUT {AdminProtocol.Suspension(casa)}");
        change.Summary.ShouldBe("Suspended the household");
    }

    // The home is the operator's own: suspending it would lock the operator out of the dashboard that
    // resumes it.
    [Fact]
    public async Task TheHomeHousehold_IsNotSuspended_AndNothingIsRecorded()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var refused = await SuspendAsync(admin, fran.TenantId, true);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("home");
        (await ListAsync(admin)).ShouldHaveSingleItem().SuspendedAt.ShouldBeNull();
        (await ChangesAsync(admin, household: null)).ShouldBeEmpty();
        (await server.VerifyAsync(await server.MintSessionAsync(fran.UserId))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AHouseholdThatDoesNotExist_Is404()
    {
        await using var server = await StartAsync();
        var admin = server.Admin(await server.MintSessionAsync((await server.MintOperatorAsync("Fran")).UserId));

        (await SuspendAsync(admin, "0123456789abcdef0123456789abcdef", true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ChangesAsync(admin, household: null)).ShouldBeEmpty();
    }

    // An owner runs their household and nothing beyond it, so not whether it runs at all.
    [Fact]
    public async Task NobodyButTheOperator_SuspendsAHousehold()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var laura = await server.SeedMemberAsync("Laura");

        foreach (var person in new[] { ana, laura })
        {
            var admin = server.Admin(await server.MintSessionAsync(person));
            (await SuspendAsync(admin, casa, true)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var operatorAdmin = server.Admin(await server.MintSessionAsync(fran.UserId));
        (await ListAsync(operatorAdmin)).Single(h => h.Id == casa).SuspendedAt.ShouldBeNull();
    }

    // Its servers' credentials stay as they were minted, live, and open nothing while it is suspended:
    // no tunnel, and no back-call made on one.
    [Fact]
    public async Task ASuspendedHouseholdsTunnelCredentials_AreRefusedAsSuspended_AndAskNothing()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        var vault = await server.MintTunnelCredentialAsync(casa, "vault");
        var voice = await server.MintTunnelCredentialAsync(casa, "voice");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        await SuspendAsync(admin, casa, true);

        var refused = await VerifyTunnelAsync(server, vault.Credential, "vault");
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await refused.Content.ReadFromJsonAsync<TunnelCredentialRefused>())!.Reason.ShouldBe(TunnelCredentialRefusal.Suspended);
        (await GuestAsync(server, voice.Credential)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The relay closes the household's open tunnels when it hears of it, and every reader of its
    // configuration drops what it kept, so the agent's next look says suspended — and, resumed, says
    // so again at once rather than a minute later.
    [Fact]
    public async Task SuspendingAndResuming_TellTheRelayAndTheReaders()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        var suspensions = await ListenAsync<HouseholdSuspended>(TunnelCredentialProtocol.SuspendedChannel);
        var saves = await ListenAsync<HouseholdConfigSaved>(HouseholdConfigProtocol.SavedChannel);

        await SuspendAsync(admin, casa, true);

        await Eventually.Until(() => suspensions.Any(s => s.TenantId == casa), "the relay to be told");
        await Eventually.Until(() => saves.Count(s => s.HouseholdId == casa) == 1, "the readers to be told");

        await SuspendAsync(admin, casa, false);

        await Eventually.Until(() => saves.Count(s => s.HouseholdId == casa) == 2, "the readers to be told it resumed");
    }

    // The suspension stands when nobody could be told, and the operator is told the household's open
    // tunnels and turns may run on for a while.
    [Fact]
    public async Task SuspendingWithNoRedisToTell_StandsWithAWarning()
    {
        await using var server = await StartAsync(withRedis: false);
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var suspended = await SuspendAsync(admin, casa, true);

        suspended.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await suspended.Content.ReadFromJsonAsync<AdminSaved<HouseholdSuspension>>())!;
        saved.Value.Suspended.ShouldBeTrue();
        saved.Warning.ShouldNotBeNull();
    }

    // Nothing was deleted: resumed, its people sign in, its servers' tunnels open, and its agents run
    // as they did, with what it saved before.
    [Fact]
    public async Task Resuming_RestoresEverything()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var anaSession = await server.MintSessionAsync(ana);
        var vault = await server.MintTunnelCredentialAsync(casa, "vault");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        await using var reader = new HouseholdConfigurationClient(
            server.Client(), AgentCredential, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens),
            redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));
        var agents = new HouseholdAgentSource(reader, new SharedServers());

        await SuspendAsync(admin, casa, true);
        (await agents.ForAsync(casa, CancellationToken.None)).Suspended.ShouldBeTrue();
        // Recorded at two instants, so the log's newest-first order is theirs, not their random ids'.
        server.Clock.Advance(TimeSpan.FromMinutes(1));

        var resumed = await SuspendAsync(admin, casa, false);

        resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resumed.Content.ReadFromJsonAsync<AdminSaved<HouseholdSuspension>>())!.Value
            .ShouldBe(new HouseholdSuspension(false));
        (await ListAsync(admin)).Single(h => h.Id == casa).SuspendedAt.ShouldBeNull();
        (await server.VerifyAsync(anaSession)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await VerifyTunnelAsync(server, vault.Credential, "vault")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.Until(
            async ValueTask<bool> () => !(await agents.ForAsync(casa, CancellationToken.None)).Suspended, "the agent to read it resumed");
        (await ChangesAsync(admin, casa)).Select(c => c.Summary).ShouldBe(["Resumed the household", "Suspended the household"]);
    }

    private static Task<HttpResponseMessage> SuspendAsync(AccountsServer.AdminClient admin, string household, bool suspended) =>
        admin.PutAsync(AdminProtocol.Suspension(household), new HouseholdSuspension(suspended));

    private static async Task<IReadOnlyList<HouseholdListEntry>> ListAsync(AccountsServer.AdminClient admin)
    {
        var response = await admin.GetAsync(AdminProtocol.Households);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<HouseholdListEntry>>())!;
    }

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin, string? household) =>
        (await (await admin.GetAsync(household is null ? AdminProtocol.AllChanges : $"{AdminProtocol.AllChanges}?household={household}"))
            .Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;

    private static Task<HttpResponseMessage> VerifyTunnelAsync(AccountsServer server, string credential, string serverName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TunnelCredentialProtocol.VerifyPath)
        {
            Content = JsonContent.Create(new TunnelCredentialQuery(credential, serverName))
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(AgentCredential));
        return server.Client().SendAsync(request);
    }

    // A household's voice server on its accounts back-call, shown by its own tunnel credential.
    private static Task<HttpResponseMessage> GuestAsync(AccountsServer server, string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "accounts/internal/guest")
        {
            Content = JsonContent.Create(new { channel = "voice" })
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return server.Client().SendAsync(request);
    }

    // Pub/sub ignores the leased database, so a test reads only what names its own household.
    private async Task<ConcurrentBag<T>> ListenAsync<T>(string channel)
    {
        var heard = new ConcurrentBag<T>();
        await redis.Connection.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal(channel),
            (_, message) => heard.Add(JsonSerializer.Deserialize<T>(message.ToString(), JsonSerializerOptions.Web)!));
        return heard;
    }
}