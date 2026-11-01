using System.Net;
using System.Net.Http.Json;
using Accounts.Store;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Domain.DTOs.WebChat;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The operator's household list (seam 1 of Tenants 4c), reached as the dashboard reaches it through
// the proxy: households created with their owner's link, listed with their people, the servers
// connected to the agent's relay and when they were last seen, and an owner's recovery link minted —
// by the operator alone, every change recorded without the link in it.
public class HouseholdListAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";

    [Fact]
    public async Task TheOperator_CreatesAHousehold_AndGetsItsOwnersLink()
    {
        await using var server = await AccountsServer.StartAsync();
        var admin = server.Admin(await server.MintSessionAsync((await server.MintOperatorAsync("Fran")).UserId));

        var created = await admin.PostAsync(AdminProtocol.Households, new NewHousehold("Casa Pérez", "Ana"));

        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        created.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var link = (await created.Content.ReadFromJsonAsync<AdminSaved<OwnerLink>>())!.Value;
        link.DisplayName.ShouldBe("Ana");
        link.ExpiresAt.ShouldBe(server.Clock.GetUtcNow().AddDays(server.Settings.LinkLifetimeDays));
        var page = await server.Client().GetAsync(new Uri(link.Url).PathAndQuery);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<strong>Ana</strong>");
        var casa = (await ListAsync(admin)).Single(h => h.Id == link.HouseholdId);
        casa.Name.ShouldBe("Casa Pérez");
        casa.IsHome.ShouldBeFalse();
        casa.Owners.ShouldBe([new HouseholdPerson(link.UserId, "Ana", false)]);
        casa.Members.ShouldBeEmpty();
        casa.LastSeenAt.ShouldBeNull();
    }

    // The link is shown once, in the answer: the change log says what was done, never the link.
    [Fact]
    public async Task CreatingAHousehold_IsRecordedOnIt_WithoutItsLink()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var link = await CreateAsync(admin, "Casa", "Ana");

        var change = (await ChangesAsync(admin, link.HouseholdId)).ShouldHaveSingleItem();
        change.ActorUserId.ShouldBe(fran.UserId);
        change.Route.ShouldBe($"POST {AdminProtocol.Households}");
        change.Summary.ShouldNotContain(new Uri(link.Url).Segments[^1]);
        change.Summary.ShouldNotContain("http");
    }

    // Nothing is seeded: a household that saved no section reads each as its defaults, so the new
    // household's first turn is built from the two shipped templates, its one space and the
    // deployment's default features.
    [Fact]
    public async Task ANewHousehold_StartsWithTheDefaults()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        var admin = server.Admin(await server.MintSessionAsync((await server.MintOperatorAsync("Fran")).UserId));

        var household = (await CreateAsync(admin, "Casa", "Ana")).HouseholdId;

        var store = server.Service<global::Accounts.Configuration.HouseholdConfigStore>();
        (await store.ReadAsync(household, HouseholdSections.SettingsSchema, CancellationToken.None))!.Name.ShouldBe("Casa");
        (await store.ReadAsync(household, HouseholdSections.SpacesSchema, CancellationToken.None))!.Spaces
            .ShouldHaveSingleItem().Slug.ShouldBe(SpaceConfig.DefaultSlug);
        (await store.ReadAsync(household, HouseholdSections.FeaturesSchema, CancellationToken.None))
            .ShouldBe(new FeaturesSection());
        await using var reader = new HouseholdConfigurationClient(
            server.Client(), AgentCredential, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens),
            redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));
        var agents = await new HouseholdAgentSource(reader, new SharedServers()).ForAsync(household, CancellationToken.None);
        agents.Agents.Select(a => a.Id).ShouldBe(["jonas", "sugary"], ignoreOrder: true);
    }

    [Fact]
    public async Task AHouseholdTheRulesRefuse_IsNotCreated_AndEachProblemIsSaid()
    {
        await using var server = await AccountsServer.StartAsync();
        var admin = server.Admin(await server.MintSessionAsync((await server.MintOperatorAsync("Fran")).UserId));

        var refused = await admin.PostAsync(AdminProtocol.Households, new NewHousehold(" ", new string('a', 201)));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.Count.ShouldBe(2);
        (await ListAsync(admin)).ShouldHaveSingleItem().IsHome.ShouldBeTrue();
        (await ChangesAsync(admin, household: null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheList_ShowsEachHouseholdsPeopleAndWhenTheyWereLastSeen()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var luis = await server.SeedPersonAsync(casa, "Luis");
        var marta = await server.SeedPersonAsync(casa, "Marta", Role.Owner);
        var started = server.Clock.GetUtcNow();
        var luisSession = await server.MintSessionAsync(luis);
        server.Clock.Advance(TimeSpan.FromHours(1));
        await server.VerifyAsync(luisSession);
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var households = await ListAsync(admin);

        var listed = households.Single(h => h.Id == casa);
        listed.Owners.ShouldBe([new HouseholdPerson(ana, "Ana", false), new HouseholdPerson(marta, "Marta", false)]);
        listed.Members.ShouldBe([new HouseholdPerson(luis, "Luis", false)]);
        listed.LastSeenAt.ShouldBe(started.AddHours(1));
        var home = households.Single(h => h.IsHome);
        home.Owners.ShouldBe([new HouseholdPerson(fran.UserId, "Fran", true)]);
    }

    // The servers are the relay's, which lives in the agent: the accounts service asks it with the
    // credential the agent already presents here, which is the agent API's own secret.
    [Fact]
    public async Task TheList_ShowsTheServersConnectedToTheAgentsRelay()
    {
        await using var agent = await AgentListingStub.StartAsync(AgentCredential);
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            AgentUrl = agent.Url
        });
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        var since = new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.Zero);
        agent.Connect(casa, new ConnectedServer("vault", 3, since, ["read_file"]));
        var admin = server.Admin(await server.MintSessionAsync((await server.MintOperatorAsync("Fran")).UserId));

        var households = await ListAsync(admin);

        households.Single(h => h.Id == casa).ConnectedServers.ShouldBe([new HouseholdConnectedServer("vault", since)]);
        households.Single(h => h.IsHome).ConnectedServers.ShouldBe([]);
    }

    // An agent that cannot be asked leaves the servers unknown, never the list unanswered.
    [Fact]
    public async Task TheList_WithoutTheAgent_SaysTheServersAreUnknown()
    {
        await using var agent = await AgentListingStub.StartAsync("another-credential");
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            AgentUrl = agent.Url
        });
        var admin = server.Admin(await server.MintSessionAsync((await server.MintOperatorAsync("Fran")).UserId));

        (await ListAsync(admin)).ShouldHaveSingleItem().ConnectedServers.ShouldBeNull();
    }

    [Fact]
    public async Task TheOperator_MintsARecoveryLinkForAnOwner_AndItIsRecordedWithoutTheLink()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var minted = await admin.PostAsync(AdminProtocol.OwnerRecovery(casa, ana));

        minted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = (await minted.Content.ReadFromJsonAsync<AdminSaved<OwnerLink>>())!.Value;
        (link.HouseholdId, link.UserId, link.DisplayName).ShouldBe((casa, ana, "Ana"));
        var page = await server.Client().GetAsync(new Uri(link.Url).PathAndQuery);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<strong>Ana</strong>");
        var change = (await ChangesAsync(admin, casa)).ShouldHaveSingleItem();
        change.Route.ShouldBe($"POST {AdminProtocol.OwnerRecovery(casa, ana)}");
        change.Summary.ShouldNotContain(new Uri(link.Url).Segments[^1]);
    }

    // A member's way back is their owners' (the members page); the operator's own is their account
    // page or the container command, so a stolen dashboard session cannot become a passkey.
    [Fact]
    public async Task ARecoveryLinkForSomebodyWhoIsNotAnotherHouseholdsOwner_IsRefused_AndRecordsNothing()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var luis = await server.SeedPersonAsync(casa, "Luis");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        (await admin.PostAsync(AdminProtocol.OwnerRecovery(casa, luis))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync(AdminProtocol.OwnerRecovery(fran.TenantId, fran.UserId))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync(AdminProtocol.OwnerRecovery(fran.TenantId, ana))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync(AdminProtocol.OwnerRecovery("nobody", ana))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ChangesAsync(admin, household: null)).ShouldBeEmpty();
    }

    // An owner manages their own household from the chat client and nothing beyond it; a member
    // manages nothing.
    [Fact]
    public async Task NobodyButTheOperator_ReachesTheHouseholdList()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var laura = await server.SeedMemberAsync("Laura");

        foreach (var person in new[] { ana, laura })
        {
            var admin = server.Admin(await server.MintSessionAsync(person));
            (await admin.GetAsync(AdminProtocol.Households)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await admin.PostAsync(AdminProtocol.Households, new NewHousehold("Mine", "Me"))).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden);
            (await admin.PostAsync(AdminProtocol.OwnerRecovery(casa, ana))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await server.Admin(session: null).GetAsync(AdminProtocol.Households)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var operatorAdmin = server.Admin(await server.MintSessionAsync(fran.UserId));
        (await ListAsync(operatorAdmin)).Count.ShouldBe(2);
        (await ChangesAsync(operatorAdmin, household: null)).ShouldBeEmpty();
    }

    private static async Task<OwnerLink> CreateAsync(AccountsServer.AdminClient admin, string name, string owner)
    {
        var created = await admin.PostAsync(AdminProtocol.Households, new NewHousehold(name, owner));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await created.Content.ReadFromJsonAsync<AdminSaved<OwnerLink>>())!.Value;
    }

    private static async Task<IReadOnlyList<HouseholdListEntry>> ListAsync(AccountsServer.AdminClient admin)
    {
        var response = await admin.GetAsync(AdminProtocol.Households);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<HouseholdListEntry>>())!;
    }

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin, string? household) =>
        (await (await admin.GetAsync(household is null ? AdminProtocol.AllChanges : $"{AdminProtocol.AllChanges}?household={household}"))
            .Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;

    // The agent's listing of a household's connected servers (ConnectedServers.Path), answered as the
    // agent answers it, behind the agent API's secret.
    private sealed class AgentListingStub(WebApplication app, Dictionary<string, List<ConnectedServer>> connected) : IAsyncDisposable
    {
        public string Url => app.Urls.First();

        public static async Task<AgentListingStub> StartAsync(string sharedSecret)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var connected = new Dictionary<string, List<ConnectedServer>>();
            app.MapGet(ConnectedServers.Path + "/{tenantId}", (string tenantId, HttpContext http) =>
                SharedSecret.Matches(http.Request.Headers.Authorization.ToString(), sharedSecret)
                    ? Results.Json(connected.GetValueOrDefault(tenantId) ?? [], TunnelProtocol.SerializerOptions)
                    : Results.Unauthorized());
            await app.StartAsync();
            return new AgentListingStub(app, connected);
        }

        public void Connect(string tenantId, ConnectedServer server)
        {
            if (!connected.TryGetValue(tenantId, out var servers))
            {
                connected[tenantId] = servers = [];
            }

            servers.Add(server);
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}