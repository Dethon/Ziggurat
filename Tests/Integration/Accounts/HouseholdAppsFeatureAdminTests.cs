using System.Net;
using System.Net.Http.Json;
using Accounts.Settings;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The operator turns the apps feature on or off for a household from the dashboard's list (seam 1):
// saved through the features section's own save and announced, so the household's agents are offered
// the apps server from their next turn, and recorded on that household. Its owners only see it
// (HouseholdFeaturesAdminTests).
public sealed class HouseholdAppsFeatureAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";

    private static readonly SharedServers _shared = new()
    {
        Sugary = "http://mcp-sugary:8080/mcp", Apps = "http://mcp-apps:8080/mcp"
    };

    [Fact]
    public async Task TheOperator_TurnsAppsOn_AndTheHouseholdsAgentsAreOfferedTheAppsServerOnTheirNextTurn()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = Reader(server);
        var agents = new HouseholdAgentSource(reader, _shared);
        (await agents.ForAsync(casa, CancellationToken.None)).Find("jonas")!.McpServerEndpoints.ShouldNotContain(_shared.Apps);
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var turnedOn = await admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(true));

        turnedOn.StatusCode.ShouldBe(HttpStatusCode.OK);
        turnedOn.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var saved = (await turnedOn.Content.ReadFromJsonAsync<AdminSaved<HouseholdAppsFeature>>())!;
        saved.Value.ShouldBe(new HouseholdAppsFeature(true));
        saved.Warning.ShouldBeNull();
        await Eventually.Until(
            async () => (await agents.ForAsync(casa, CancellationToken.None)).Find("jonas")!.McpServerEndpoints.Contains(_shared.Apps),
            "the announced save to reach the agent's reader");
        (await agents.ForAsync(casa, CancellationToken.None)).Find("jonas")!.McpServerEndpoints
            .ShouldContain(_shared.Sugary, "the household's other features are left as they were");
        var households = await ListAsync(admin);
        households.Single(h => h.Id == casa).Apps.ShouldBeTrue();
        households.Single(h => h.IsHome).Apps.ShouldBeFalse();
        var change = (await ChangesAsync(admin, casa)).ShouldHaveSingleItem();
        change.ActorUserId.ShouldBe(fran.UserId);
        change.Route.ShouldBe($"PUT {AdminProtocol.AppsFeature(casa)}");
        change.Summary.ShouldBe("Turned the apps feature on");
    }

    [Fact]
    public async Task TheOperator_TurnsAppsOff_AndTheHouseholdsAgentsLoseTheAppsServer()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = Reader(server);
        var agents = new HouseholdAgentSource(reader, _shared);
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        await admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(true));
        await Eventually.Until(
            async () => (await agents.ForAsync(casa, CancellationToken.None)).Find("jonas")!.McpServerEndpoints.Contains(_shared.Apps),
            "apps turned on");
        // Recorded at two instants, so the log's newest-first order is theirs, not their random ids'.
        server.Clock.Advance(TimeSpan.FromMinutes(1));

        var turnedOff = await admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(false));

        turnedOff.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await turnedOff.Content.ReadFromJsonAsync<AdminSaved<HouseholdAppsFeature>>())!.Value.ShouldBe(new HouseholdAppsFeature(false));
        await Eventually.Until(
            async () => !(await agents.ForAsync(casa, CancellationToken.None)).Find("jonas")!.McpServerEndpoints.Contains(_shared.Apps),
            "apps turned off");
        (await ListAsync(admin)).Single(h => h.Id == casa).Apps.ShouldBeFalse();
        (await ChangesAsync(admin, casa)).Select(c => c.Summary)
            .ShouldBe(["Turned the apps feature off", "Turned the apps feature on"]);
    }

    // Asking again is how the operator retries an announcement that did not reach the agent, so it is
    // saved and announced again; it changed nothing, so nothing is recorded.
    [Fact]
    public async Task TurningAppsOnAgain_RecordsNothingMore()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        await admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(true));

        var again = await admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(true));

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ChangesAsync(admin, casa)).ShouldHaveSingleItem().Summary.ShouldBe("Turned the apps feature on");
    }

    // Only apps is the operator's to change here: the features the owners set stay as they set them.
    [Fact]
    public async Task TurningAppsOn_KeepsTheFeaturesTheOwnersSet()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var owner = server.Admin(await server.MintSessionAsync(ana));
        await owner.PutAsync(AdminProtocol.Features, new FeaturesSection { Memory = false, Idealista = true });
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        await admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(true));

        (await FeaturesAsync(owner)).ShouldBe(new FeaturesSection { Memory = false, Idealista = true, Apps = true });
    }

    // Off until the operator says otherwise: a household that never saved its features, and one whose
    // owners saved them, each read apps as off — on the operator's list and on the owners' page alike.
    [Fact]
    public async Task AHouseholdTheOperatorNeverTurnedAppsOnFor_ReadsItAsOff()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var (pinos, bea) = await server.SeedHouseholdAsync("Pinos", "Bea");
        var beas = server.Admin(await server.MintSessionAsync(bea));
        await beas.PutAsync(AdminProtocol.Features, new { memory = false });
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var households = await ListAsync(admin);

        households.Single(h => h.Id == casa).Apps.ShouldBeFalse();
        households.Single(h => h.Id == pinos).Apps.ShouldBeFalse();
        (await FeaturesAsync(server.Admin(await server.MintSessionAsync(ana)))).Apps.ShouldBeFalse();
        (await FeaturesAsync(beas)).Apps.ShouldBeFalse();
    }

    [Fact]
    public async Task AHouseholdThatDoesNotExist_Is404_AndNothingIsRecorded()
    {
        await using var server = await StartAsync();
        var admin = server.Admin(await server.MintSessionAsync((await server.MintOperatorAsync("Fran")).UserId));
        const string nobody = "0123456789abcdef0123456789abcdef";

        var refused = await admin.PutAsync(AdminProtocol.AppsFeature(nobody), new HouseholdAppsFeature(true));

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("no household");
        (await ChangesAsync(admin, nobody)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NoAppsFeatureSent_IsRefused_AndNothingChanges()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, _) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var refused = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.AppsFeature(casa))
        {
            Content = JsonContent.Create<HouseholdAppsFeature?>(null)
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListAsync(admin)).Single(h => h.Id == casa).Apps.ShouldBeFalse();
        (await ChangesAsync(admin, casa)).ShouldBeEmpty();
    }

    // The owners see it and cannot change it, here any more than in their features save.
    [Fact]
    public async Task NobodyButTheOperator_TurnsAppsOn()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var laura = await server.SeedPersonAsync(casa, "Laura", global::Accounts.Store.Role.Member);

        foreach (var person in new[] { ana, laura })
        {
            var admin = server.Admin(await server.MintSessionAsync(person));
            (await admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(true))).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden);
        }

        var operatorAdmin = server.Admin(await server.MintSessionAsync(fran.UserId));
        (await ListAsync(operatorAdmin)).Single(h => h.Id == casa).Apps.ShouldBeFalse();
        (await ChangesAsync(operatorAdmin, casa)).ShouldBeEmpty();
    }

    // An owner's change reads the household's configuration and saves it whole, keeping apps as it read
    // it: the operator's change, landing in between, would be undone. So it waits for the household's
    // turn — held here by an owner's key save whose provider has not answered yet.
    [Fact]
    public async Task TheOperatorsChange_WaitsForTheHouseholdsChangeInFlight()
    {
        await using var providers = await FakeKeyProviders.StartAsync();
        const string key = "sk-or-v1-0123456789abcdef0123456789abcdef-work";
        providers.Accept(key);
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            KeyProbes = new KeyProbeSettings { OpenRouterUrl = providers.OpenRouterUrl }
        });
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var owner = server.Admin(await server.MintSessionAsync(ana));
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        var release = providers.Hold(key);
        var keySave = owner.PutAsync(AdminProtocol.OpenRouterKey, new OpenRouterKey(key));
        await Eventually.Until(() => providers.WasAsked(key), "the owner's key save is in flight");

        var turnedOn = admin.PutAsync(AdminProtocol.AppsFeature(casa), new HouseholdAppsFeature(true));
        await Eventually.Settle();

        turnedOn.IsCompleted.ShouldBeFalse("the household's change in flight goes first");
        release();
        (await keySave).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await turnedOn).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(admin)).Single(h => h.Id == casa).Apps.ShouldBeTrue();
    }

    private Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });

    // The agent's own reader, keeping what it read for longer than any test runs: only a save's
    // announcement makes it read again.
    private HouseholdConfigurationClient Reader(AccountsServer server) =>
        new(server.Client(), AgentCredential, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens),
            redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));

    private static async Task<FeaturesSection> FeaturesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.Features)).Content.ReadFromJsonAsync<FeaturesSection>())!;

    private static async Task<IReadOnlyList<HouseholdListEntry>> ListAsync(AccountsServer.AdminClient admin)
    {
        var response = await admin.GetAsync(AdminProtocol.Households);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<HouseholdListEntry>>())!;
    }

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin, string household) =>
        (await (await admin.GetAsync($"{AdminProtocol.AllChanges}?household={household}"))
            .Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
}