using Accounts.Commands;
using Accounts.Store;
using Domain.Contracts;
using Domain.DTOs.Household;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Clients.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// What a reader of a household's configuration — the agent, a shared server — keeps of it, against a
// running accounts service and a real Redis: a view is kept until a save of its household is
// announced, or until its lifetime runs out when an announcement never came, and a save of one
// household drops nothing of another's.
public class HouseholdConfigurationClientTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string Agent = "agent-credential";
    private const string Scheduling = "scheduling-credential";
    private const string Telegram = "telegram-credential";

    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    private Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["agent"] = Agent,
                ["mcp-scheduling"] = Scheduling,
                ["mcp-channel-telegram"] = Telegram
            },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });

    [Fact]
    public async Task AView_IsKeptUntilASaveOfItsHouseholdIsAnnounced()
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(server, household, HouseholdSections.Settings, """{ "name": "Casa", "timeZone": "Europe/Madrid" }""");
        await using var client = Client(server, Scheduling);
        var proof = Proof(household, person);

        var first = await client.SettingsAsync(household, proof, CancellationToken.None);
        await WriteUnannouncedAsync(server, household, "Atlantic/Canary");
        var kept = await client.SettingsAsync(household, proof, CancellationToken.None);
        await SetAsync(server, household, HouseholdSections.Settings, """{ "name": "Casa", "timeZone": "Asia/Tokyo" }""");

        first.TimeZone.ShouldBe("Europe/Madrid");
        kept.TimeZone.ShouldBe("Europe/Madrid");
        await Eventually.Until(async () => (await client.SettingsAsync(household, proof, CancellationToken.None)).TimeZone == "Asia/Tokyo",
            "the announced save to drop what was kept");
    }

    // A host that shows a household's configuration live (the chat hub's agent picker) is told of a
    // save once what this client kept of it is gone, so what it asks for next is the save's.
    [Fact]
    public async Task ASave_IsToldToItsListeners_AfterWhatWasKeptOfItIsDropped()
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(server, household, HouseholdSections.Settings, """{ "name": "Casa", "timeZone": "Europe/Madrid" }""");
        await using var client = Client(server, Scheduling);
        var proof = Proof(household, person);
        var told = new List<(HouseholdConfigSaved Saved, string ReadThen)>();
        await client.ListenAsync(CancellationToken.None);
        // The channel is the deployment's, so a busy suite's other households are heard on it too.
        client.Saved += saved =>
        {
            if (saved.HouseholdId != household)
            {
                return;
            }

            var now = client.SettingsAsync(household, proof, CancellationToken.None).GetAwaiter().GetResult();
            lock (told)
            {
                told.Add((saved, now.TimeZone));
            }
        };
        await client.SettingsAsync(household, proof, CancellationToken.None);

        await SetAsync(server, household, HouseholdSections.Settings, """{ "name": "Casa", "timeZone": "Asia/Tokyo" }""");

        await Eventually.Until(() => { lock (told) { return told.Count > 0; } }, "the save to be told");
        lock (told)
        {
            told[0].Saved.ShouldBe(new HouseholdConfigSaved(household, HouseholdSections.Settings));
            told[0].ReadThen.ShouldBe("Asia/Tokyo");
        }
    }

    [Fact]
    public async Task ASaveOfAnotherHousehold_DropsNothingOfThisOnes()
    {
        await using var server = await StartAsync();
        var (madrid, ana) = await server.SeedHouseholdAsync("Madrid", "Ana");
        var (canarias, luis) = await server.SeedHouseholdAsync("Canarias", "Luis");
        await using var client = Client(server, Scheduling);
        await client.SettingsAsync(madrid, Proof(madrid, ana), CancellationToken.None);
        await client.SettingsAsync(canarias, Proof(canarias, luis), CancellationToken.None);

        await WriteUnannouncedAsync(server, madrid, "Atlantic/Canary");
        await SetAsync(server, canarias, HouseholdSections.Settings, """{ "name": "Canarias", "timeZone": "Atlantic/Canary" }""");

        await Eventually.Until(async () =>
                (await client.SettingsAsync(canarias, Proof(canarias, luis), CancellationToken.None)).TimeZone == "Atlantic/Canary",
            "the save of Canarias to reach the client");
        (await client.SettingsAsync(madrid, Proof(madrid, ana), CancellationToken.None)).TimeZone.ShouldBe(TimeZoneInfo.Local.Id);
    }

    // An announcement missed — Redis away, or a host with none — is bounded by the lifetime.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AView_IsAskedAgainOnceItsLifetimeRunsOut(bool listening)
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var client = Client(server, Scheduling, clock: clock, listening: listening);
        await client.SettingsAsync(household, Proof(household, person), CancellationToken.None);
        await WriteUnannouncedAsync(server, household, "Atlantic/Canary");

        var kept = await client.SettingsAsync(household, Proof(household, person), CancellationToken.None);
        clock.Advance(_lifetime + TimeSpan.FromSeconds(1));
        var asked = await client.SettingsAsync(household, Proof(household, person), CancellationToken.None);

        kept.TimeZone.ShouldBe(TimeZoneInfo.Local.Id);
        asked.TimeZone.ShouldBe("Atlantic/Canary");
    }

    // The Telegram server's view spans every household, so any household's keys saved drop it.
    [Fact]
    public async Task TheTelegramBots_AreAskedAgainWhenAnyHouseholdsKeysAreSaved()
    {
        await using var server = await StartAsync();
        var home = (await server.MintOperatorAsync("Fran")).TenantId;
        var casa = await server.SeedHouseholdAsync("Casa");
        await SetAsync(server, home, HouseholdSections.Keys, """{ "telegramBots": { "jack": { "token": "1:jack" } } }""");
        await using var client = Client(server, Telegram);

        var before = await client.TelegramBotsAsync(CancellationToken.None);
        await SetAsync(server, casa, HouseholdSections.Keys, """{ "telegramBots": { "jonas": { "token": "2:casa" } } }""");

        before.ShouldBe([new HouseholdTelegramBot(home, "jack", "1:jack")]);
        await Eventually.Until(async () => (await client.TelegramBotsAsync(CancellationToken.None)).Count == 2,
            "the casa household's bot to be answered");
    }

    // The bot lists a household's agents on the assertion of the person asking, and an agent renamed
    // reaches the list as soon as the save is announced.
    [Fact]
    public async Task TheTelegramBot_ReadsAHouseholdsAgentNamesOnAPersonsAssertion()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        await using var client = Client(server, Telegram);
        var proof = HouseholdProof.OfAssertion(server.Service<global::Accounts.Signing.Assertions>().OfGuest(household, "telegram"));

        var starting = await client.AgentNamesAsync(household, proof, CancellationToken.None);
        await SetAsync(server, household, HouseholdSections.Agents,
            """{ "agents": [ { "id": "chef", "template": "jonas", "name": "Chef" } ], "defaultAgent": "chef" }""");

        starting.Agents.Select(a => a.Id).ShouldBe(["jonas", "sugary"]);
        await Eventually.Until(
            async () => (await client.AgentNamesAsync(household, proof, CancellationToken.None)).Agents
                .Select(a => (a.Id, a.Name)).SequenceEqual([("chef", "Chef")]),
            "the saved agents to be listed");
    }

    [Fact]
    public async Task TheAgent_ReadsAnyHouseholdByIdWithItsOwnToken()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        await SetAsync(server, household, HouseholdSections.Keys, """{ "openRouter": { "apiKey": "sk-or-casa" } }""");
        await using var agent = Client(server, Agent, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens));
        await using var notTheAgent = Client(server, Agent);

        var config = await agent.ForAgentAsync(household, CancellationToken.None);

        config.HouseholdId.ShouldBe(household);
        config.OpenRouterApiKey.ShouldBe("sk-or-casa");
        await Should.ThrowAsync<InvalidOperationException>(() => notTheAgent.ForAgentAsync(household, CancellationToken.None));
    }

    // Dreaming's walk: every household and its people, asked with the agent's own token.
    [Fact]
    public async Task TheAgent_ReadsEveryHouseholdsPeopleWithItsOwnToken()
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Luis");
        await using var agent = Client(server, Agent, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens));
        await using var notTheAgent = Client(server, Agent);

        var people = await agent.PeopleAsync(CancellationToken.None);

        people.ShouldContain(h => h.HouseholdId == household && h.People.SequenceEqual(new[] { person }));
        await Should.ThrowAsync<InvalidOperationException>(() => notTheAgent.PeopleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AProofOfAnotherHousehold_IsRefusedAndNothingIsKeptUnderTheExpectedOne()
    {
        await using var server = await StartAsync();
        var (madrid, _) = await server.SeedHouseholdAsync("Madrid", "Ana");
        var (canarias, luis) = await server.SeedHouseholdAsync("Canarias", "Luis");
        await using var client = Client(server, Scheduling);

        var refused = await Should.ThrowAsync<HouseholdConfigRefusedException>(
            () => client.SettingsAsync(madrid, Proof(canarias, luis), CancellationToken.None));
        var unproven = await Should.ThrowAsync<HouseholdConfigRefusedException>(
            () => client.SettingsAsync(madrid, HouseholdProof.OfCallToken("not.a.token"), CancellationToken.None));

        refused.Refusal.Reason.ShouldBe(HouseholdConfigRefusal.Unproven);
        unproven.Refusal.Reason.ShouldBe(HouseholdConfigRefusal.Unproven);
    }

    private HouseholdConfigurationClient Client(
        AccountsServer server, string credential, Func<string>? agentToken = null, TimeProvider? clock = null,
        bool listening = true) =>
        new(server.Client(), credential, agentToken, listening ? redis.Connection : null, clock ?? TimeProvider.System, _lifetime);

    private static HouseholdProof Proof(string household, string person) =>
        HouseholdProof.OfCallToken(McpTestSecret.CallTokens.For(
            IdentityClaims.Person(person, household, "signalr", ["owner"]), TokenAudiences.CallTo(ServerAddress.Named("mcp-scheduling"))));

    private static async Task SetAsync(AccountsServer server, string household, string section, string document)
    {
        var (exit, output) = await server.RunAsync(server.Settings, document,
            AccountsCommand.SetHouseholdConfig, "--household", household, "--section", section, "--file", "-");
        exit.ShouldBe(0, output);
    }

    // A change nobody announced, as if the announcement had been lost on its way.
    private static async Task WriteUnannouncedAsync(AccountsServer server, string household, string timeZone)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        var document = $$"""{ "schemaVersion": 1, "name": "Casa", "timeZone": "{{timeZone}}" }""";
        var row = await db.HouseholdSections.SingleOrDefaultAsync(s => s.TenantId == household && s.Section == HouseholdSections.Settings);
        if (row is null)
        {
            db.HouseholdSections.Add(new HouseholdSection
            {
                Id = Tokens.NewId(), TenantId = household, Section = HouseholdSections.Settings, Document = document
            });
        }
        else
        {
            row.Document = document;
        }

        await db.SaveChangesAsync();
    }
}