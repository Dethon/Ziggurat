using System.Net;
using System.Net.Http.Json;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household's features through the admin API (seam 1 of Tenants 4c): an owner turns a shared
// server or a domain feature off or on, saved through the features section's own save, and the
// household's agents are built without it on the next turn. The turn side is the agent's own reader
// of the household's configuration against a real Redis, as the agent builds a turn's agents from it.
public sealed class HouseholdFeaturesAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";

    // Where this deployment runs its shared servers, as the agent's settings name them.
    private static readonly SharedServers _shared = new()
    {
        Scheduling = "http://mcp-scheduling:8080/mcp", Timers = "http://mcp-timers:8080/mcp"
    };

    [Fact]
    public async Task AnOwner_ReadsTheHouseholdsFeatures_AsItsTurnsUseThem()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");

        var features = await FeaturesAsync(server.Admin(await server.MintSessionAsync(ana)));

        features.ShouldBe(new FeaturesSection(), "a household that saved none has every feature on but Idealista");
    }

    [Fact]
    public async Task AFeatureTurnedOff_IsGoneFromTheHouseholdsAgentsOnTheNextTurn()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = Reader(server);
        var agents = new HouseholdAgentSource(reader, _shared);
        var jonas = (await agents.ForAsync(household, CancellationToken.None)).Find("jonas").ShouldNotBeNull();
        jonas.McpServerEndpoints.ShouldContain(_shared.Scheduling);
        jonas.EnabledFeatures.ShouldContain("memory");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var saved = await admin.PutAsync(AdminProtocol.Features, new FeaturesSection { Scheduling = false, Memory = false });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await saved.Content.ReadFromJsonAsync<AdminSaved<FeaturesSection>>())!;
        answer.Value.ShouldBe(new FeaturesSection { Scheduling = false, Memory = false });
        answer.Warning.ShouldBeNull();
        await Eventually.Until(
            async () => !(await agents.ForAsync(household, CancellationToken.None)).Find("jonas")!.McpServerEndpoints
                .Contains(_shared.Scheduling),
            "the announced save to reach the agent's reader");
        (await agents.ForAsync(household, CancellationToken.None)).Find("jonas")!.ShouldSatisfyAllConditions(
            a => a.EnabledFeatures.ShouldNotContain("memory"),
            a => a.McpServerEndpoints.ShouldContain(_shared.Timers, "a feature left on keeps its server"));
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Changed the scheduling, memory features"]);
    }

    [Fact]
    public async Task AFeatureTurnedBackOn_IsTheHouseholdsAgentsAgain()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = Reader(server);
        var agents = new HouseholdAgentSource(reader, _shared);
        var admin = server.Admin(await server.MintSessionAsync(ana));
        await admin.PutAsync(AdminProtocol.Features, new FeaturesSection { Workers = false });
        await Eventually.Until(
            async () => !(await agents.ForAsync(household, CancellationToken.None)).Find("jonas")!.EnabledFeatures.Contains("subagents"),
            "workers turned off");

        (await admin.PutAsync(AdminProtocol.Features, new FeaturesSection())).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Eventually.Until(
            async () => (await agents.ForAsync(household, CancellationToken.None)).Find("jonas")!.EnabledFeatures.Contains("subagents"),
            "workers turned back on");
    }

    // The section's own rules hold whoever saves it: a feature this build does not know is no
    // feature, refused in the rules' words, and nothing is saved or recorded.
    [Fact]
    public async Task AFeatureThisBuildDoesNotKnow_IsRefused_AndNothingChanges()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Features, new { scheduling = false, teleport = true });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldNotBeEmpty();
        (await FeaturesAsync(admin)).ShouldBe(new FeaturesSection());
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AMember_IsRefusedTheFeatures_AndChangesNothing()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.Admin(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await laura.GetAsync(AdminProtocol.Features)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.PutAsync(AdminProtocol.Features, new FeaturesSection { Memory = false })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
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

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
}