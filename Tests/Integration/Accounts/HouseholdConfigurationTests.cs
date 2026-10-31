using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Commands;
using Accounts.Signing;
using Accounts.Store;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security;
using Domain.Security.Sealing;
using Domain.Security.Signing;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A household's configuration as the accounts service keeps and answers it (seam 1): sections stored
// and carried forward to their current shape, secrets sealed at rest and unsealed only into the view
// of the server they belong to, the household taken from what the asker was handed for it and never
// from what it names, a save announced to every reader, and the master key rotated without
// re-sealing a secret.
public class HouseholdConfigurationTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string Agent = "agent-credential";
    private const string Idealista = "idealista-credential";
    private const string Scheduling = "scheduling-credential";
    private const string SignalR = "signalr-credential";
    private const string Telegram = "telegram-credential";

    private const string OpenRouterKey = "sk-or-v1-household-openrouter-key";
    private const string IdealistaKey = "idealista-api-key-of-the-household";
    private const string IdealistaSecret = "idealista-secret-of-the-household";
    private const string JackToken = "111111:jack-bot-token-of-the-household";

    private static readonly string Keys = $$"""
        {
          "schemaVersion": 1,
          "openRouter": { "apiKey": "{{OpenRouterKey}}" },
          "idealista": { "apiKey": "{{IdealistaKey}}", "secret": "{{IdealistaSecret}}" },
          "telegramBots": { "jack": { "token": "{{JackToken}}" } }
        }
        """;

    private Task<AccountsServer> StartAsync(string? masterKey = null, string? directory = null) =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["agent"] = Agent,
                ["mcp-idealista"] = Idealista,
                ["mcp-scheduling"] = Scheduling,
                ["mcp-channel-signalr"] = SignalR,
                ["mcp-channel-telegram"] = Telegram
            },
            Redis = new() { ConnectionString = redis.ConnectionString },
            HouseholdConfig = new() { MasterKey = masterKey ?? AccountsServer.MasterKey }
        }, directory);

    [Fact]
    public async Task ASectionSavedUnderAnOldSchemaVersion_IsReadMigratedForward()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        // Version 0: a document from before it named its version, written as the store keeps one.
        await using (var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync())
        {
            db.HouseholdSections.Add(new HouseholdSection
            {
                Id = Tokens.NewId(), TenantId = household, Section = HouseholdSections.Settings,
                Document = """{ "name": "Casa Crespo", "timeZone": "Atlantic/Canary" }"""
            });
            await db.SaveChangesAsync();
        }

        var config = await AgentViewAsync(server, household);
        var (exit, printed) = await server.RunAsync(
            AccountsCommand.GetHouseholdConfig, "--household", household, "--section", HouseholdSections.Settings);

        config.Settings.ShouldBe(new HouseholdSettings { Name = "Casa Crespo", TimeZone = "Atlantic/Canary" });
        exit.ShouldBe(0, printed);
        JsonDocument.Parse(printed).RootElement.GetProperty(SectionSchema.VersionProperty).GetInt32()
            .ShouldBe(HouseholdSections.SettingsSchema.CurrentVersion);
    }

    // A file kept from before is saved as today's shape, so the store never holds an old one it wrote.
    [Fact]
    public async Task ASectionSetUnderAnOldSchemaVersion_IsStoredAtTheCurrentOne()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");

        var (exit, output) = await SetAsync(server, household, HouseholdSections.Features, """{ "idealista": true }""");

        exit.ShouldBe(0, output);
        var stored = JsonDocument.Parse(await StoredAsync(server, household, HouseholdSections.Features)).RootElement;
        stored.GetProperty(SectionSchema.VersionProperty).GetInt32().ShouldBe(HouseholdSections.FeaturesSchema.CurrentVersion);
        stored.GetProperty("idealista").GetBoolean().ShouldBeTrue();
        stored.GetProperty("memory").GetBoolean().ShouldBeTrue();
    }

    // A household made by somebody who configured nothing yet still has a configuration to read.
    [Fact]
    public async Task AHouseholdThatSavedNothing_ReadsAsEachSectionsDefaults()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");

        var config = await AgentViewAsync(server, household);

        config.ShouldSatisfyAllConditions(
            () => config.IsHome.ShouldBeFalse(),
            () => config.Settings.Name.ShouldBe("Casa"),
            () => config.Settings.TimeZone.ShouldBe(TimeZoneInfo.Local.Id),
            () => config.Agents.Agents.ShouldBeEmpty(),
            () => config.Features.ShouldBe(new FeaturesSection()),
            () => config.Spaces.Spaces.ShouldBe([HouseholdSpace.Default]),
            () => config.OpenRouterApiKey.ShouldBeNull());
    }

    [Fact]
    public async Task ASecret_IsNeverInTheStoreInTheClear()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");

        var (exit, output) = await SetAsync(server, household, HouseholdSections.Keys, Keys);

        exit.ShouldBe(0, output);
        output.ShouldNotContain(OpenRouterKey);
        await using (var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync())
        {
            (await db.HouseholdSections.SingleAsync(s => s.Section == HouseholdSections.Keys)).Document.ShouldContain("\"sealed\"");
        }

        // The write-ahead log too, where SQLite keeps what it has not checkpointed yet.
        var files = Directory.GetFiles(server.Settings.DataDirectory, Path.GetFileName(server.Settings.DatabasePath) + "*");
        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            foreach (var secret in new[] { OpenRouterKey, IdealistaKey, IdealistaSecret, JackToken })
            {
                bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)).ShouldBe(-1, $"{secret} in {file}");
            }
        }
    }

    [Fact]
    public async Task EachSecret_IsUnsealedOnlyIntoTheViewOfTheServerItBelongsTo()
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);

        var agent = await RawAsync(server, Agent, HouseholdConfigProtocol.AgentView, new HouseholdConfigQuery(household, AgentToken()));
        var idealista = await RawAsync(server, Idealista, HouseholdConfigProtocol.IdealistaView,
            new HouseholdConfigQuery(CallToken: CallToken("mcp-idealista", household, person)));
        var scheduling = await RawAsync(server, Scheduling, HouseholdConfigProtocol.SettingsView,
            new HouseholdConfigQuery(CallToken: CallToken("mcp-scheduling", household, person)));
        var bots = await RawAsync(server, Telegram, HouseholdConfigProtocol.TelegramBotsView, new { });

        agent.ShouldSatisfyAllConditions(
            () => agent.ShouldContain(OpenRouterKey),
            () => agent.ShouldNotContain(IdealistaKey),
            () => agent.ShouldNotContain(IdealistaSecret),
            () => agent.ShouldNotContain(JackToken));
        JsonSerializer.Deserialize<HouseholdConfigAnswer<IdealistaKeys>>(idealista, JsonSerializerOptions.Web)
            .ShouldBe(new HouseholdConfigAnswer<IdealistaKeys>(household, new IdealistaKeys(IdealistaKey, IdealistaSecret)));
        idealista.ShouldNotContain(OpenRouterKey);
        idealista.ShouldNotContain(JackToken);
        foreach (var secret in new[] { OpenRouterKey, IdealistaKey, IdealistaSecret, JackToken })
        {
            scheduling.ShouldNotContain(secret);
        }

        JsonSerializer.Deserialize<HouseholdTelegramBots>(bots, JsonSerializerOptions.Web)!.Bots
            .ShouldBe([new HouseholdTelegramBot(household, "jack", JackToken)]);
        bots.ShouldNotContain(OpenRouterKey);
        bots.ShouldNotContain(IdealistaKey);
    }

    public static TheoryData<string, string> AnotherServersViews => new()
    {
        { Scheduling, HouseholdConfigProtocol.IdealistaView },
        { Scheduling, HouseholdConfigProtocol.AgentView },
        { Idealista, HouseholdConfigProtocol.AgentView },
        { Idealista, HouseholdConfigProtocol.SettingsView },
        { Telegram, HouseholdConfigProtocol.AgentView },
        { Telegram, HouseholdConfigProtocol.IdealistaView },
        { SignalR, HouseholdConfigProtocol.TelegramBotsView },
        { SignalR, HouseholdConfigProtocol.AgentNamesView },
        { Scheduling, HouseholdConfigProtocol.AgentNamesView },
        { Agent, HouseholdConfigProtocol.IdealistaView },
        { Agent, HouseholdConfigProtocol.TelegramBotsView }
    };

    [Theory]
    [MemberData(nameof(AnotherServersViews))]
    public async Task ACallerAskingForAnotherServersView_Is403(string credential, string view)
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);

        var response = await PostAsync(server, credential, view,
            new HouseholdConfigQuery(CallToken: CallToken("mcp-idealista", household, person)));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(IdealistaKey);
    }

    [Fact]
    public async Task ACallerPresentingNoKnownCredential_Is401()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");

        var response = await PostAsync(server, "nobody's", HouseholdConfigProtocol.AgentView,
            new HouseholdConfigQuery(household, AgentToken()));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The household is whichever the call the server is serving was signed for, never one it names:
    // a leaked server credential holding no such token reads nothing of anyone's.
    [Fact]
    public async Task ASharedServer_IsAnsweredTheHouseholdItsCallTokenNames_AndCannotNameAnother()
    {
        await using var server = await StartAsync();
        var (madrid, ana) = await server.SeedHouseholdAsync("Madrid", "Ana");
        var (canarias, _) = await server.SeedHouseholdAsync("Canarias", "Luis");
        await SetAsync(server, madrid, HouseholdSections.Settings, """{ "name": "Madrid", "timeZone": "Europe/Madrid" }""");
        await SetAsync(server, canarias, HouseholdSections.Settings, """{ "name": "Canarias", "timeZone": "Atlantic/Canary" }""");

        var answered = await AskAsync<HouseholdConfigAnswer<HouseholdSettings>>(server, Scheduling, HouseholdConfigProtocol.SettingsView,
            new HouseholdConfigQuery(CallToken: CallToken("mcp-scheduling", madrid, ana)));
        var naming = await PostAsync(server, Scheduling, HouseholdConfigProtocol.SettingsView,
            new HouseholdConfigQuery(canarias, CallToken("mcp-scheduling", madrid, ana)));
        var unproven = await PostAsync(server, Scheduling, HouseholdConfigProtocol.SettingsView,
            new HouseholdConfigQuery(canarias));

        answered.ShouldBe(new HouseholdConfigAnswer<HouseholdSettings>(
            madrid, new HouseholdSettings { Name = "Madrid", TimeZone = "Europe/Madrid" }));
        (await RefusalAsync(naming, HttpStatusCode.BadRequest)).Reason.ShouldBe(HouseholdConfigRefusal.BadQuery);
        (await RefusalAsync(unproven, HttpStatusCode.BadRequest)).Reason.ShouldBe(HouseholdConfigRefusal.BadQuery);
    }

    public static TheoryData<string, string> TokensThatProveNothing => new()
    {
        // Signed by the agent for another server: that server's, not this one's.
        { "another server's", "WrongAudience" },
        // Signed by a key the agent does not publish.
        { "forged", "UnknownKey" },
        // The agent's own plumbing, which acts for no household.
        { "nobody's", "names nobody" },
        { "garbage", "Malformed" }
    };

    [Theory]
    [MemberData(nameof(TokensThatProveNothing))]
    public async Task ACallTokenThatDoesNotProveTheHousehold_IsRefusedAsUnproven(string kind, string why)
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);
        var token = kind switch
        {
            "another server's" => CallToken("mcp-scheduling", household, person),
            "forged" => new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Accounts)).Sign(
                IdentityClaims.Person(person, household, "signalr", ["owner"])
                    .Stamped(TokenIssuers.Agent, "mcp-idealista", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2))),
            "nobody's" => McpTestSecret.CallTokens.For(IdentityClaims.Nobody("signalr"), "mcp-idealista"),
            _ => "not.a.token"
        };

        var response = await PostAsync(server, Idealista, HouseholdConfigProtocol.IdealistaView, new HouseholdConfigQuery(CallToken: token));

        var refusal = await RefusalAsync(response, HttpStatusCode.NotFound);
        refusal.Reason.ShouldBe(HouseholdConfigRefusal.Unproven);
        refusal.Detail.ShouldContain(why);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(IdealistaKey);
    }

    // A schedule fires with no call behind it: the delegation the agent signed for its last write is
    // what names its household, to the scheduling server alone and for a schedule alone.
    [Fact]
    public async Task AScheduleDelegation_ProvesTheHouseholdToTheSchedulingServerAlone()
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(server, household, HouseholdSections.Settings, """{ "name": "Casa", "timeZone": "Asia/Tokyo" }""");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);
        var account = new Account(household, person);
        var schedule = TestDelegations.For(account, "signalr", new DelegatedObject(DelegatedObject.Schedule, "nightly", "{}"));
        var watch = TestDelegations.For(account, "signalr", new DelegatedObject(DelegatedObject.Watch, "nightly", "{}"));

        var answered = await AskAsync<HouseholdConfigAnswer<HouseholdSettings>>(server, Scheduling, HouseholdConfigProtocol.SettingsView,
            new HouseholdConfigQuery(Delegation: schedule));
        var aWatch = await PostAsync(server, Scheduling, HouseholdConfigProtocol.SettingsView, new HouseholdConfigQuery(Delegation: watch));
        var toIdealista = await PostAsync(server, Idealista, HouseholdConfigProtocol.IdealistaView, new HouseholdConfigQuery(Delegation: schedule));
        var twoProofs = await PostAsync(server, Scheduling, HouseholdConfigProtocol.SettingsView,
            new HouseholdConfigQuery(CallToken: CallToken("mcp-scheduling", household, person), Delegation: schedule));

        answered.ShouldBe(new HouseholdConfigAnswer<HouseholdSettings>(household, new HouseholdSettings { Name = "Casa", TimeZone = "Asia/Tokyo" }));
        (await RefusalAsync(aWatch, HttpStatusCode.NotFound)).Reason.ShouldBe(HouseholdConfigRefusal.Unproven);
        (await RefusalAsync(toIdealista, HttpStatusCode.BadRequest)).Reason.ShouldBe(HouseholdConfigRefusal.BadQuery);
        (await toIdealista.Content.ReadAsStringAsync()).ShouldNotContain(IdealistaKey);
        (await RefusalAsync(twoProofs, HttpStatusCode.BadRequest)).Reason.ShouldBe(HouseholdConfigRefusal.BadQuery);
    }

    // The chat hub holds the accounts service's assertion of its connection's person, not a call token.
    [Fact]
    public async Task AnAssertion_ProvesTheHouseholdOnTheAskersOwnChannelAlone()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        await SetAsync(server, household, HouseholdSections.Spaces,
            """{ "spaces": [ { "slug": "default", "name": "Cocina", "accentColor": "#00aa00", "defaultAgent": "chef" } ] }""");
        var assertions = server.Service<Assertions>();

        var spaces = await AskAsync<HouseholdConfigAnswer<SpacesSection>>(server, SignalR, HouseholdConfigProtocol.SpacesView,
            new HouseholdConfigQuery(Assertion: assertions.OfGuest(household, "signalr")));
        var telegrams = await PostAsync(server, SignalR, HouseholdConfigProtocol.SpacesView,
            new HouseholdConfigQuery(Assertion: assertions.OfGuest(household, "telegram")));

        spaces.HouseholdId.ShouldBe(household);
        spaces.Section.Spaces.ShouldBe([
            new HouseholdSpace { Slug = "default", Name = "Cocina", AccentColor = "#00aa00", DefaultAgent = "chef" }
        ]);
        (await RefusalAsync(telegrams, HttpStatusCode.NotFound)).Reason.ShouldBe(HouseholdConfigRefusal.Unproven);
    }

    // The shared Telegram bot lists and switches among a household's agents for a chat, so it reads
    // their ids and names — and nothing else of their definitions — on the assertion of the person
    // asking. A household that saved no agents lists the two templates it runs.
    [Fact]
    public async Task TheTelegramBot_ReadsTheNamesOfTheAgentsOfThePersonsHousehold()
    {
        await using var server = await StartAsync();
        var named = await server.SeedHouseholdAsync("Casa");
        var fresh = await server.SeedHouseholdAsync("Nueva");
        await SetAsync(server, named, HouseholdSections.Agents, """
            {
              "defaultAgent": "chef",
              "agents": [
                { "id": "chef", "template": "jonas", "name": "Chef", "customInstructions": "Cook." },
                { "id": "sweet", "template": "sugary", "name": "Dulce" }
              ]
            }
            """);
        var assertions = server.Service<Assertions>();

        var agents = await AskAsync<HouseholdConfigAnswer<HouseholdAgentNames>>(server, Telegram, HouseholdConfigProtocol.AgentNamesView,
            new HouseholdConfigQuery(Assertion: assertions.OfGuest(named, "telegram")));
        var starting = await AskAsync<HouseholdConfigAnswer<HouseholdAgentNames>>(server, Telegram, HouseholdConfigProtocol.AgentNamesView,
            new HouseholdConfigQuery(Assertion: assertions.OfGuest(fresh, "telegram")));
        var raw = await RawAsync(server, Telegram, HouseholdConfigProtocol.AgentNamesView,
            new HouseholdConfigQuery(Assertion: assertions.OfGuest(named, "telegram")));

        agents.HouseholdId.ShouldBe(named);
        agents.Section.DefaultAgent.ShouldBe("chef");
        agents.Section.Agents.ShouldBe([new HouseholdAgentName("chef", "Chef"), new HouseholdAgentName("sweet", "Dulce")]);
        raw.ShouldNotContain("Cook.");
        starting.Section.DefaultAgent.ShouldBe("jonas");
        starting.Section.Agents.Select(a => a.Id).ShouldBe(["jonas", "sugary"]);
    }

    // The agent signs every proof there is, so it names the household itself — for a turn, the
    // household of the identity it verified; for dreaming and memory extraction, the one the work
    // carries — and proves only that it is the agent: its credential here is SexyTime's server's too.
    [Fact]
    public async Task TheAgent_NamesAnyHousehold_ButOnlyWithATokenOfItsOwn()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);

        var signed = await AskAsync<AgentHouseholdConfig>(server, Agent, HouseholdConfigProtocol.AgentView,
            new HouseholdConfigQuery(household, AgentToken()));
        var unsigned = await PostAsync(server, Agent, HouseholdConfigProtocol.AgentView, new HouseholdConfigQuery(household));
        var anotherAudience = await PostAsync(server, Agent, HouseholdConfigProtocol.AgentView,
            new HouseholdConfigQuery(household, McpTestSecret.CallTokens.For(IdentityClaims.Nobody("signalr"), "mcp-idealista")));

        signed.OpenRouterApiKey.ShouldBe(OpenRouterKey);
        foreach (var refused in new[] { unsigned, anotherAudience })
        {
            (await RefusalAsync(refused, HttpStatusCode.NotFound)).Reason.ShouldBe(HouseholdConfigRefusal.Unproven);
            (await refused.Content.ReadAsStringAsync()).ShouldNotContain(OpenRouterKey);
        }
    }

    [Fact]
    public async Task AHouseholdThisServiceDoesNotKeep_IsRefusedAsUnknown()
    {
        await using var server = await StartAsync();
        const string stranger = "0123456789abcdef0123456789abcdef";

        var byId = await PostAsync(server, Agent, HouseholdConfigProtocol.AgentView, new HouseholdConfigQuery(stranger, AgentToken()));
        var byToken = await PostAsync(server, Scheduling, HouseholdConfigProtocol.SettingsView,
            new HouseholdConfigQuery(CallToken: CallToken("mcp-scheduling", stranger, "fedcba9876543210fedcba9876543210")));

        (await RefusalAsync(byId, HttpStatusCode.NotFound)).Reason.ShouldBe(HouseholdConfigRefusal.UnknownHousehold);
        (await RefusalAsync(byToken, HttpStatusCode.NotFound)).Reason.ShouldBe(HouseholdConfigRefusal.UnknownHousehold);
    }

    [Fact]
    public async Task TheHomeHousehold_IsSaidToBeTheHome()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        (await AgentViewAsync(server, fran.TenantId)).IsHome.ShouldBeTrue();
    }

    // The agent's catalogue for its channels, and a turn that acts for nobody, are the home's: it asks
    // for the home by name, before any turn could tell it the home's id, and is answered the home's
    // view under its real id.
    [Fact]
    public async Task TheAgent_AskingForTheHome_IsAnsweredTheHomesView()
    {
        await using var server = await StartAsync();
        var home = (await server.MintOperatorAsync("Fran")).TenantId;

        var config = await AgentViewAsync(server, HouseholdConfigProtocol.HomeHousehold);

        config.HouseholdId.ShouldBe(home);
        config.IsHome.ShouldBeTrue();
    }

    // Dreaming has no call behind it: the agent walks every household and its people, each on that
    // household's key, so it is answered every household at once — the one that has nobody yet too —
    // proving with its own token that it is the agent, and no other caller is answered at all.
    [Fact]
    public async Task TheAgent_IsAnsweredEveryHouseholdAndItsPeople_OnlyWithATokenOfItsOwn()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var ana = await server.SeedMemberAsync("Ana");
        var (casa, luis) = await server.SeedHouseholdAsync("Casa", "Luis");
        var empty = await server.SeedHouseholdAsync("Vacía");

        var people = await AskAsync<HouseholdPeople>(server, Agent, HouseholdConfigProtocol.PeopleView,
            new HouseholdConfigQuery(CallToken: AgentToken()));
        var unsigned = await PostAsync(server, Agent, HouseholdConfigProtocol.PeopleView, new HouseholdConfigQuery());
        var naming = await PostAsync(server, Agent, HouseholdConfigProtocol.PeopleView, new HouseholdConfigQuery(casa, AgentToken()));
        var another = await PostAsync(server, Telegram, HouseholdConfigProtocol.PeopleView, new HouseholdConfigQuery(CallToken: AgentToken()));

        people.Households.Select(h => (h.HouseholdId, People: string.Join(",", h.People.Order())))
            .ShouldBe([
                (fran.TenantId, string.Join(",", new[] { fran.UserId, ana }.Order())),
                (casa, luis),
                (empty, "")
            ], ignoreOrder: true);
        (await RefusalAsync(unsigned, HttpStatusCode.NotFound)).Reason.ShouldBe(HouseholdConfigRefusal.Unproven);
        (await RefusalAsync(naming, HttpStatusCode.BadRequest)).Reason.ShouldBe(HouseholdConfigRefusal.BadQuery);
        another.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // The Telegram server polls every bot from its start, so it is answered every household's at once.
    [Fact]
    public async Task TheTelegramServer_IsAnsweredEveryHouseholdsBots()
    {
        await using var server = await StartAsync();
        var home = (await server.MintOperatorAsync("Fran")).TenantId;
        var casa = await server.SeedHouseholdAsync("Casa");
        await SetAsync(server, home, HouseholdSections.Keys,
            """{ "telegramBots": { "jack": { "token": "1:jack" }, "jonas": { "token": "2:jonas" } } }""");
        await SetAsync(server, casa, HouseholdSections.Keys, """{ "telegramBots": { "jonas": { "token": "3:casa" } } }""");

        var bots = await AskAsync<HouseholdTelegramBots>(server, Telegram, HouseholdConfigProtocol.TelegramBotsView, new { });

        bots.Bots.ShouldBe([
            new HouseholdTelegramBot(home, "jack", "1:jack"),
            new HouseholdTelegramBot(home, "jonas", "2:jonas"),
            new HouseholdTelegramBot(casa, "jonas", "3:casa")
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task ASave_IsAnnouncedToEveryReader()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        var heard = await ListenAsync(household);

        var (exit, output) = await SetAsync(server, household, HouseholdSections.Features, """{ "schemaVersion": 1, "memory": false }""");

        exit.ShouldBe(0, output);
        output.ShouldContain("readers were told");
        await Eventually.Until(() => heard.Contains(new HouseholdConfigSaved(household, HouseholdSections.Features)), "the save's announcement");
    }

    [Fact]
    public async Task ASaveWithNoRedisToTellItsReaders_StandsAndSaysSo()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = Agent }
        });
        var household = await server.SeedHouseholdAsync("Casa");

        var (exit, output) = await SetAsync(server, household, HouseholdSections.Features, """{ "memory": false }""");

        exit.ShouldBe(1, output);
        output.ShouldContain("not told");
        (await AgentViewAsync(server, household)).Features.Memory.ShouldBeFalse();
    }

    [Fact]
    public async Task Get_PrintsEverySecretAsSealed_AndNeverItsValue()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);

        var (exit, output) = await server.RunAsync(
            AccountsCommand.GetHouseholdConfig, "--household", household, "--section", HouseholdSections.Keys);

        exit.ShouldBe(0, output);
        foreach (var secret in new[] { OpenRouterKey, IdealistaKey, IdealistaSecret, JackToken })
        {
            output.ShouldNotContain(secret);
        }

        var printed = JsonDocument.Parse(output).RootElement;
        printed.GetProperty("openRouter").GetProperty("apiKey").GetProperty("sealed").GetBoolean().ShouldBeTrue();
        printed.GetProperty("telegramBots").GetProperty("jack").GetProperty("token").GetProperty("sealed").GetBoolean().ShouldBeTrue();
    }

    // What get prints can be edited and set again without retyping a key it did not change.
    [Fact]
    public async Task Set_KeepsAStoredSecretWhereTheFileSaysSealed()
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);
        var printed = JsonNode.Parse((await server.RunAsync(
            AccountsCommand.GetHouseholdConfig, "--household", household, "--section", HouseholdSections.Keys)).Output)!;
        printed["idealista"]!["secret"] = "a-new-idealista-secret";

        var (exit, output) = await SetAsync(server, household, HouseholdSections.Keys, printed.ToJsonString());

        exit.ShouldBe(0, output);
        (await AgentViewAsync(server, household)).OpenRouterApiKey.ShouldBe(OpenRouterKey);
        (await AskAsync<HouseholdConfigAnswer<IdealistaKeys>>(server, Idealista, HouseholdConfigProtocol.IdealistaView,
                new HouseholdConfigQuery(CallToken: CallToken("mcp-idealista", household, person)))).Section
            .ShouldBe(new IdealistaKeys(IdealistaKey, "a-new-idealista-secret"));
        (await AskAsync<HouseholdTelegramBots>(server, Telegram, HouseholdConfigProtocol.TelegramBotsView, new { })).Bots
            .ShouldBe([new HouseholdTelegramBot(household, "jack", JackToken)]);
    }

    public static TheoryData<string, string, string> DocumentsThatAreNotTheirSection => new()
    {
        { HouseholdSections.Settings, """{ "name": "Casa", "timeZone": "Mars/Olympus" }""", "Mars/Olympus" },
        { HouseholdSections.Settings, """{ "name": "Casa", "timezone": "Europe/Madrid", "tz": "x" }""", "tz" },
        { HouseholdSections.Features, """{ "schemaVersion": 99 }""", "schema version 99" },
        { HouseholdSections.Agents, """{ "agents": [ { "id": "jonas" } ] }""", "exactly one" },
        { HouseholdSections.Agents,
            """{ "agents": [ { "id": "jack", "definition": { "id": "jack", "name": "Jack", "model": "m", "mcpServerEndpoints": [], "telegramBotToken": "1:x" } } ] }""",
            "keys section" },
        { HouseholdSections.Agents, """{ "defaultAgent": "nabu", "agents": [] }""", "nabu" },
        { HouseholdSections.Spaces, """{ "spaces": [] }""", "at least one space" },
        { HouseholdSections.Keys, """{ "openRouter": { "apiKey": { "sealed": "AgAAAA==" } } }""", "cannot be set" },
        { HouseholdSections.Keys, """{ "openRouter": { "apiKey": { "sealed": true } } }""", "none is stored" }
    };

    [Theory]
    [MemberData(nameof(DocumentsThatAreNotTheirSection))]
    public async Task Set_RefusesADocumentThatIsNotItsSection_AndSavesNothing(string section, string document, string why)
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");

        var (exit, output) = await SetAsync(server, household, section, document);

        exit.ShouldBe(2, output);
        output.ShouldContain("nothing was saved");
        output.ShouldContain(why);
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.HouseholdSections.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Set_WithASecretAndNoMasterKey_SavesNothing()
    {
        await using var server = await AccountsServer.StartAsync(s => s with { HouseholdConfig = new() });
        var household = await server.SeedHouseholdAsync("Casa");

        var (exit, output) = await SetAsync(server, household, HouseholdSections.Keys, Keys);

        exit.ShouldBe(1, output);
        output.ShouldContain("no master key");
        output.ShouldNotContain(OpenRouterKey);
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.HouseholdSections.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Set_ForAHouseholdThatDoesNotExist_SavesNothing()
    {
        await using var server = await StartAsync();

        var (exit, output) = await SetAsync(server, "0123456789abcdef0123456789abcdef", HouseholdSections.Features, "{}");

        exit.ShouldBe(1, output);
        output.ShouldContain("no household");
    }

    // A stack being set up — the smoke stack, a test stack — knows its home only as the home, and the
    // operator need not look its id up to configure their own household.
    [Fact]
    public async Task SetAndGet_TheHouseholdCalledHome_IsTheHomeHousehold()
    {
        await using var server = await StartAsync();
        var home = (await server.MintOperatorAsync("Fran")).TenantId;

        var (exit, output) = await SetAsync(server, "home", HouseholdSections.Features, """{ "idealista": true }""");
        var (_, printed) = await server.RunAsync(server.Settings, "",
            AccountsCommand.GetHouseholdConfig, "--household", "home", "--section", HouseholdSections.Features);

        exit.ShouldBe(0, output);
        output.ShouldContain(home);
        (await AgentViewAsync(server, home)).Features.Idealista.ShouldBeTrue();
        printed.ShouldContain("\"idealista\": true");
    }

    // Rotation rewraps each household's data key and touches no sealed field: the stored sections are
    // byte for byte what they were, and open under the new master alone.
    [Fact]
    public async Task RotatingTheMasterKey_RewrapsTheDataKeys_AndEverySecretStillOpens()
    {
        var previous = AccountsServer.MasterKey;
        var current = Convert.ToBase64String(SealingKey.Generate().Export());
        await using var before = await StartAsync(previous);
        var household = await before.SeedHouseholdAsync("Casa");
        await SetAsync(before, household, HouseholdSections.Keys, Keys);
        var sealedBefore = await StoredAsync(before, household, HouseholdSections.Keys);
        var rotated = before.Settings with { HouseholdConfig = new() { MasterKey = current } };

        var (exit, output) = await before.RunAsync(rotated, previous + "\n", AccountsCommand.RewrapHouseholdKeys);
        var rerun = await before.RunAsync(rotated, previous, AccountsCommand.RewrapHouseholdKeys);

        exit.ShouldBe(0, output);
        output.ShouldContain("Rewrapped 1 household data key");
        output.ShouldNotContain(previous);
        rerun.Exit.ShouldBe(0, rerun.Output);
        rerun.Output.ShouldContain("Rewrapped 0 household data key");
        (await StoredAsync(before, household, HouseholdSections.Keys)).ShouldBe(sealedBefore);
        await using (var after = await StartAsync(current, before.Directory))
        {
            (await AgentViewAsync(after, household)).OpenRouterApiKey.ShouldBe(OpenRouterKey);
        }

        // The service still holding the previous key says why it cannot answer rather than guessing.
        var stale = await PostAsync(before, Agent, HouseholdConfigProtocol.AgentView, new HouseholdConfigQuery(household, AgentToken()));
        (await RefusalAsync(stale, HttpStatusCode.ServiceUnavailable)).Reason.ShouldBe(HouseholdConfigRefusal.SecretsUnavailable);
    }

    [Fact]
    public async Task RewrappingWithAKeyThatWrappedNothing_ChangesNothing()
    {
        await using var server = await StartAsync();
        var household = await server.SeedHouseholdAsync("Casa");
        await SetAsync(server, household, HouseholdSections.Keys, Keys);
        var stranger = Convert.ToBase64String(SealingKey.Generate().Export());
        var rotated = server.Settings with { HouseholdConfig = new() { MasterKey = Convert.ToBase64String(SealingKey.Generate().Export()) } };

        var (exit, output) = await server.RunAsync(rotated, stranger, AccountsCommand.RewrapHouseholdKeys);

        exit.ShouldBe(1, output);
        output.ShouldContain("neither");
        (await AgentViewAsync(server, household)).OpenRouterApiKey.ShouldBe(OpenRouterKey);
    }

    private static string AgentToken() =>
        McpTestSecret.CallTokens.For(
            IdentityClaims.Nobody(HouseholdConfigProtocol.AgentTokenChannel), TokenAudiences.HouseholdConfiguration);

    // The agent's call token for one server, acting for a person of the household, as that server
    // was handed it.
    private static string CallToken(string server, string household, string person) =>
        McpTestSecret.CallTokens.For(IdentityClaims.Person(person, household, "signalr", ["owner"]),
            TokenAudiences.CallTo(ServerAddress.Named(server)));

    private static async Task<AgentHouseholdConfig> AgentViewAsync(AccountsServer server, string household) =>
        await AskAsync<AgentHouseholdConfig>(server, Agent, HouseholdConfigProtocol.AgentView, new HouseholdConfigQuery(household, AgentToken()));

    private static async Task<T> AskAsync<T>(AccountsServer server, string credential, string view, object query)
    {
        var response = await PostAsync(server, credential, view, query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web))!;
    }

    private static async Task<string> RawAsync(AccountsServer server, string credential, string view, object query)
    {
        var response = await PostAsync(server, credential, view, query);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return body;
    }

    private static Task<HttpResponseMessage> PostAsync(AccountsServer server, string credential, string view, object query)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, HouseholdConfigProtocol.PathOf(view))
        {
            Content = JsonContent.Create(query)
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return server.Client().SendAsync(request);
    }

    private static async Task<HouseholdConfigRefused> RefusalAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        return JsonSerializer.Deserialize<HouseholdConfigRefused>(body, JsonSerializerOptions.Web)!;
    }

    private static Task<(int Exit, string Output)> SetAsync(AccountsServer server, string household, string section, string document) =>
        server.RunAsync(server.Settings, document,
            AccountsCommand.SetHouseholdConfig, "--household", household, "--section", section, "--file", "-");

    private static async Task<string> StoredAsync(AccountsServer server, string household, string section)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        return (await db.HouseholdSections.SingleAsync(s => s.TenantId == household && s.Section == section)).Document;
    }

    // Pub/sub ignores the leased database, so a test hears only the saves naming its own household.
    private async Task<ConcurrentBag<HouseholdConfigSaved>> ListenAsync(string household)
    {
        var heard = new ConcurrentBag<HouseholdConfigSaved>();
        await redis.Connection.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal(HouseholdConfigProtocol.SavedChannel),
            (_, message) =>
            {
                var saved = JsonSerializer.Deserialize<HouseholdConfigSaved>(message.ToString(), JsonSerializerOptions.Web)!;
                if (saved.HouseholdId == household)
                {
                    heard.Add(saved);
                }
            });
        return heard;
    }
}