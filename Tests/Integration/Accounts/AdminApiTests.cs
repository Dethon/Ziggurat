using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Commands;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The admin API (seam 1 of Tenants 4c), reached as the chat client reaches it through the proxy:
// each request is put to the session check for the admin channel first and carries what it answered.
// Who may do what is decided by the assertion the check signed — the owner role of the household it
// names, or the operator role — and never by a parameter, the plain headers or the cookie alone.
public class AdminApiTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";

    [Fact]
    public async Task AnOwner_ReadsTheHouseholdsSettings()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var response = await server.Admin(await server.MintSessionAsync(fran.UserId)).GetAsync(AdminProtocol.Settings);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var settings = (await response.Content.ReadFromJsonAsync<HouseholdSettings>())!;
        settings.Name.ShouldBe("Home");
        settings.TimeZone.ShouldBe(TimeZoneInfo.Local.Id);
        settings.Language.ShouldBeNull();
    }

    // The same section the operator's command reads, so the two never disagree about the household.
    [Fact]
    public async Task AnOwnersSave_IsWhatTheHouseholdThenReads()
    {
        await using var server = await AccountsServer.StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var saved = await admin.PutAsync(AdminProtocol.Settings, Pérez);

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await saved.Content.ReadFromJsonAsync<AdminSaved<HouseholdSettings>>())!;
        answer.Value.ShouldBe(Pérez);
        answer.Warning.ShouldNotBeNull("nothing was told: this server has no Redis");
        (await (await admin.GetAsync(AdminProtocol.Settings)).Content.ReadFromJsonAsync<HouseholdSettings>()).ShouldBe(Pérez);
        var (_, printed) = await server.RunAsync(AccountsCommand.GetHouseholdConfig, "--household", household, "--section", HouseholdSections.Settings);
        HouseholdSections.SettingsSchema.Read(JsonNode.Parse(printed)!.AsObject()).ShouldBe(Pérez);
    }

    [Fact]
    public async Task SettingsTheRulesRefuse_AreNotSaved_AndEachProblemIsSaid()
    {
        await using var server = await AccountsServer.StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Settings, new { name = "Casa", timeZone = "Mars/Olympus", language = "español!" });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var refusal = (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!;
        refusal.Problems.Count.ShouldBe(2);
        refusal.Problems.ShouldContain(p => p.Contains("Mars/Olympus"));
        (await (await admin.GetAsync(AdminProtocol.Settings)).Content.ReadFromJsonAsync<HouseholdSettings>())!
            .TimeZone.ShouldBe(TimeZoneInfo.Local.Id);
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AMember_IsRefusedEveryRoute_AndChangesNothing()
    {
        await using var server = await AccountsServer.StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.Admin(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await laura.GetAsync(AdminProtocol.Settings)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.PutAsync(AdminProtocol.Settings, Pérez)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.GetAsync(AdminProtocol.HouseholdChanges)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.GetAsync(AdminProtocol.AllChanges)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await HomeSettingsAsync(server)).Name.ShouldBe("Home");
    }

    // No route names a household: an owner acts on the one their assertion names, whatever the plain
    // headers beside it say, so another household's owner reaches only their own.
    [Fact]
    public async Task AnotherHouseholdsOwner_ChangesOnlyTheirOwnHousehold()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var request = new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Settings) { Content = JsonContent.Create(Pérez) };
        request.Headers.Add(AdminProtocol.RequestHeader, AdminProtocol.RequestHeaderValue);
        request.Headers.Add(ForwardedAccount.AssertionHeader, await AssertionAsync(server, await server.MintSessionAsync(ana), AppChannels.Admin));
        request.Headers.Add(ForwardedAccount.UserHeader, fran.UserId);
        request.Headers.Add(ForwardedAccount.TenantHeader, fran.TenantId);
        request.Headers.Add(ForwardedAccount.RolesHeader, "owner,operator");

        var saved = await server.Client().SendAsync(request);

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HomeSettingsAsync(server)).Name.ShouldBe("Home");
        (await server.Service<global::Accounts.Configuration.HouseholdConfigStore>()
            .ReadAsync(casa, HouseholdSections.SettingsSchema, CancellationToken.None))!.Name.ShouldBe(Pérez.Name);
    }

    // Never the cookie alone, never the plain headers, and never an assertion signed for another
    // channel: the hub's or the dashboard's opens no admin route.
    [Theory]
    [InlineData("the cookie alone")]
    [InlineData("the plain headers")]
    [InlineData("an assertion for the chat hub")]
    [InlineData("an assertion for the dashboard")]
    [InlineData("a forged assertion")]
    public async Task ARequestWithoutTheAdminAssertion_IsRefused(string carrying)
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var session = await server.MintSessionAsync(fran.UserId);
        var request = new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Settings) { Content = JsonContent.Create(Pérez) };
        request.Headers.Add(AdminProtocol.RequestHeader, AdminProtocol.RequestHeaderValue);
        request.Headers.Add("Cookie", $"ziggurat_session={session}");
        switch (carrying)
        {
            case "the plain headers":
                request.Headers.Add(ForwardedAccount.UserHeader, fran.UserId);
                request.Headers.Add(ForwardedAccount.TenantHeader, fran.TenantId);
                request.Headers.Add(ForwardedAccount.RolesHeader, "owner,operator");
                break;
            case "an assertion for the chat hub":
                request.Headers.Add(ForwardedAccount.AssertionHeader, await AssertionAsync(server, session, ChannelIds.SignalR));
                break;
            case "an assertion for the dashboard":
                request.Headers.Add(ForwardedAccount.AssertionHeader, await AssertionAsync(server, session, AppChannels.Dashboard));
                break;
            case "a forged assertion":
                request.Headers.Add(ForwardedAccount.AssertionHeader,
                    TestAssertions.Forged(new Account(fran.TenantId, fran.UserId), AppChannels.Admin));
                break;
        }

        var refused = await server.Client().SendAsync(request);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await HomeSettingsAsync(server)).Name.ShouldBe("Home");
    }

    [Fact]
    public async Task ASignedOutRequest_IsRefusedByTheSessionCheck()
    {
        await using var server = await AccountsServer.StartAsync();

        (await server.Admin(session: null).GetAsync(AdminProtocol.Settings)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // A sibling host's page sends the session cookie too, since every host under the parent domain is
    // the same site, but it cannot send the admin client's header without being refused a preflight.
    [Fact]
    public async Task ARequestWithoutTheAdminClientsHeader_IsRefused_AndChangesNothing()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var refused = await admin.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Settings) { Content = JsonContent.Create(Pérez) },
            withAdminHeader: false);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await HomeSettingsAsync(server)).Name.ShouldBe("Home");
    }

    // What a cross-site form could send is no save, even carrying everything else a save carries.
    [Fact]
    public async Task AWriteThatIsNotJson_IsRefused_AndChangesNothing()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var form = new FormUrlEncodedContent([KeyValuePair.Create("name", "Casa"), KeyValuePair.Create("timeZone", "UTC")]);

        var refused = await server.Admin(await server.MintSessionAsync(fran.UserId))
            .SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Settings) { Content = form });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await HomeSettingsAsync(server)).Name.ShouldBe("Home");
    }

    [Fact]
    public async Task AnOwner_IsRefusedTheOperatorsRoutes_AndTheOperatorIsAnswered()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");

        (await server.Admin(await server.MintSessionAsync(ana)).GetAsync(AdminProtocol.AllChanges))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await server.Admin(await server.MintSessionAsync(fran.UserId)).GetAsync(AdminProtocol.AllChanges))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // A path under neither root is nobody's, whoever asks.
    [Fact]
    public async Task APathUnderNeitherRoot_IsNoRoute()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        (await server.Admin(await server.MintSessionAsync(fran.UserId)).GetAsync($"{AdminProtocol.Root}/settings"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Who, what and when: the household's owners read their own household's changes, and the
    // operator every household's.
    [Fact]
    public async Task EveryChange_IsRecordedWithWhoAndWhen()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var anaAdmin = server.Admin(await server.MintSessionAsync(ana));
        var franAdmin = server.Admin(await server.MintSessionAsync(fran.UserId));
        var first = server.Clock.GetUtcNow();
        await anaAdmin.PutAsync(AdminProtocol.Settings, Pérez);
        server.Clock.Advance(TimeSpan.FromMinutes(5));
        await anaAdmin.PutAsync(AdminProtocol.Settings, Pérez with { Language = "es" });
        server.Clock.Advance(TimeSpan.FromMinutes(5));
        await franAdmin.PutAsync(AdminProtocol.Settings, new { name = "Hogar", timeZone = TimeZoneInfo.Local.Id });

        var changes = await ChangesAsync(anaAdmin);

        changes.Select(c => c.Summary).ShouldBe(["Changed the language", "Changed the name, time zone, language"]);
        changes.ShouldAllBe(c => c.HouseholdId == casa && c.ActorUserId == ana && c.ActorName == "Ana"
                                 && c.Route == $"PUT {AdminProtocol.Settings}");
        changes.Select(c => c.At).ShouldBe([first.AddMinutes(5), first]);
        var all = (await (await franAdmin.GetAsync(AdminProtocol.AllChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
        all.Select(c => (c.HouseholdId, c.ActorName)).ShouldBe([(fran.TenantId, "Fran"), (casa, "Ana"), (casa, "Ana")]);
        var casaOnly = (await (await franAdmin.GetAsync($"{AdminProtocol.AllChanges}?household={casa}"))
            .Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
        casaOnly.Count.ShouldBe(2);
    }

    // The turn side: the agent's reader of the household's configuration has the settings it read
    // before, and the save announces itself, so the agents the household's next turn is built from
    // carry the new zone and language.
    [Fact]
    public async Task ASavedSetting_ReachesTheAgentsTheHouseholdsNextTurnIsBuiltFrom()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = new HouseholdConfigurationClient(
            server.Client(), AgentCredential, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens),
            redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));
        var agents = new HouseholdAgentSource(reader, new SharedServers());
        (await agents.ForAsync(household, CancellationToken.None)).TimeZone.ShouldBe(TimeZoneInfo.Local.Id);

        var saved = await server.Admin(await server.MintSessionAsync(ana)).PutAsync(AdminProtocol.Settings, Pérez);

        (await saved.Content.ReadFromJsonAsync<AdminSaved<HouseholdSettings>>())!.Warning.ShouldBeNull();
        await Eventually.Until(async () => (await agents.ForAsync(household, CancellationToken.None)).TimeZone == Pérez.TimeZone,
            "the announced save to reach the agent's reader");
        (await agents.ForAsync(household, CancellationToken.None)).Agents
            .ShouldAllBe(agent => agent.Language == Pérez.Language);
    }

    private static readonly HouseholdSettings Pérez = new() { Name = "Casa Pérez", TimeZone = "America/New_York", Language = "fr" };

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;

    private static async Task<HouseholdSettings> HomeSettingsAsync(AccountsServer server)
    {
        var store = server.Service<global::Accounts.Configuration.HouseholdConfigStore>();
        var home = (await store.HomeAsync(CancellationToken.None))!;
        return (await store.ReadAsync(home.Id, HouseholdSections.SettingsSchema, CancellationToken.None))!;
    }

    // What the session check signs for another route, read off its answer as the proxy would.
    private static async Task<string> AssertionAsync(AccountsServer server, string session, string channel) =>
        (await server.VerifyAsync(session, channel: channel)).Headers.GetValues(ForwardedAccount.AssertionHeader).Single();
}