using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household's spaces through the admin API (seam 1 of Tenants 4c): an owner adds, renames,
// recolours, reorders and removes spaces and picks the agent each opens on, saved through the spaces
// section's own save. The chat side is the spaces view the hub reads (ChatHub.GetSpaces), on the
// person's assertion for its channel, as the hub asks it.
public sealed class HouseholdSpacesAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";
    private const string HubCredential = "hub-credential";

    [Fact]
    public async Task AnOwner_ReadsTheHouseholdsSpaces_AndTheAgentsTheyMayOpenOn()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");

        var spaces = await SpacesAsync(server.Admin(await server.MintSessionAsync(ana)));

        spaces.Spaces.ShouldBe([HouseholdSpace.Default]);
        spaces.Agents.ShouldBe([new AdminSpaceAgent("jonas", "Jonas"), new AdminSpaceAgent("sugary", "Sugary")]);
    }

    [Fact]
    public async Task ANewAndAnEditedSpace_AreWhatTheChatClientIsToldOfItsHousehold()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var hub = HubReader(server);
        var proof = HouseholdProof.OfAssertion(server.Service<global::Accounts.Signing.Assertions>().OfGuest(household, ChannelIds.SignalR));
        (await hub.SpacesAsync(household, proof, CancellationToken.None)).Spaces.ShouldBe([HouseholdSpace.Default]);
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var kitchen = new HouseholdSpace { Slug = "kitchen", Name = "Cocina", AccentColor = "#2a9d8f", DefaultAgent = "sugary", Order = 0 };
        var main = HouseholdSpace.Default with { Name = "Casa", AccentColor = "#112233", Order = 1 };

        var saved = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [main, kitchen] });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await saved.Content.ReadFromJsonAsync<AdminSaved<AdminSpaces>>())!;
        answer.Value.Spaces.ShouldBe([kitchen, main], "in their order");
        answer.Warning.ShouldBeNull();
        await Eventually.Until(async () => (await hub.SpacesAsync(household, proof, CancellationToken.None)).Spaces.Count == 2,
            "the announced save to reach the hub's reader");
        (await hub.SpacesAsync(household, proof, CancellationToken.None)).Spaces.ShouldBe([main, kitchen]);
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Added space kitchen; changed space default's name, colour, order"]);
    }

    [Fact]
    public async Task ARemovedSpace_IsNoLongerTheHouseholds()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var attic = new HouseholdSpace { Slug = "attic", Name = "Desván", Order = 1 };
        await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default, attic] });
        server.Clock.Advance(TimeSpan.FromMinutes(1));

        var saved = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection { Spaces = [HouseholdSpace.Default] });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Removed space attic", "Added space attic"]);
    }

    // The default space is where a removed space's topics are listed, where an alarm's push lands
    // and what the chat's own address opens: it is renamed and recoloured, never removed.
    [Fact]
    public async Task TheDefaultSpace_IsNotRemoved_AndSaysWhy()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [new HouseholdSpace { Slug = "kitchen", Name = "Cocina" }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("Main");
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    // A space opens on one of the household's agents or on none; the section's own rules check only
    // that it names an id.
    [Fact]
    public async Task ASpaceOpeningOnAnAgentTheHouseholdDoesNotHave_IsRefused_AndNothingChanges()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default with { DefaultAgent = "butler" }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem().ShouldContain("butler");
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Kitchen", "#2a9d8f", "Cocina")]
    [InlineData("kitchen", "teal", "Cocina")]
    [InlineData("kitchen", "#2a9d8f", " ")]
    public async Task ASpaceTheRulesRefuse_IsNotSaved(string slug, string colour, string name)
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = slug, Name = name, AccentColor = colour, Order = 1 }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldNotBeEmpty();
        (await SpacesAsync(admin)).Spaces.ShouldBe([HouseholdSpace.Default]);
    }

    [Fact]
    public async Task ASpaceNameOverTheLimit_IsRefused()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default with { Name = new string('a', AdminSpaces.NameLength + 1) }]
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem()
            .ShouldContain(AdminSpaces.NameLength.ToString());
    }

    // Removing the agent a space opens on would leave the space opening on nothing the household has:
    // refused, as removing the household's default agent is, naming the space to change first.
    [Fact]
    public async Task AnAgentASpaceOpensOn_IsNotRemoved_AndSaysWhichSpace()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = "kitchen", Name = "Cocina", DefaultAgent = "sugary", Order = 1 }]
        });

        var refused = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, AdminProtocol.Agent("sugary")));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("Cocina");
        (await (await admin.GetAsync(AdminProtocol.Agents)).Content.ReadFromJsonAsync<AdminAgents>())!.Agents
            .Select(a => a.Id).ShouldBe(["jonas", "sugary"]);
        await admin.PutAsync(AdminProtocol.Spaces, new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = "kitchen", Name = "Cocina", Order = 1 }]
        });
        (await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, AdminProtocol.Agent("sugary")))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AMember_IsRefusedTheSpaces_AndChangesNothing()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.Admin(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await laura.GetAsync(AdminProtocol.Spaces)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.PutAsync(AdminProtocol.Spaces, new SpacesSection())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential, ["mcp-channel-signalr"] = HubCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });

    // The chat hub's own reader, keeping what it read for longer than any test runs: only a save's
    // announcement makes it read again.
    private HouseholdConfigurationClient HubReader(AccountsServer server) =>
        new(server.Client(), HubCredential, null, redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));

    private static async Task<AdminSpaces> SpacesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.Spaces)).Content.ReadFromJsonAsync<AdminSpaces>())!;

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
}