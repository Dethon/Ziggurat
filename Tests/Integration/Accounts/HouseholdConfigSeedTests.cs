using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Store;
using Domain.DTOs.Household;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The migration's way to give a household a section it never saved (Accounts/Internal/HouseholdConfigSeedApi):
// admitted for the migration's own credential alone, a dry run saving nothing, and a section the
// household already saved left as it is.
public sealed class HouseholdConfigSeedTests
{
    private const string Credential = "migration-credential";
    private const string TelegramCredential = "telegram-credential";
    private const string Seed = "/accounts/internal/household-config-seed";

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["household-migration"] = Credential, ["mcp-channel-telegram"] = TelegramCredential
            }
        });

    private static readonly JsonObject _kitchen = HouseholdSections.SpacesSchema.Write(
        new SpacesSection { Spaces = [HouseholdSpace.Default with { Slug = "kitchen", Name = "Kitchen" }] });

    [Fact]
    public async Task AnotherCallerOfTheInternalApi_IsRefused()
    {
        await using var server = await StartAsync();

        var response = await SeedAsync(server, await HomeAsync(server), _kitchen, dryRun: false, TelegramCredential);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SectionsAsync(server)).ShouldBe(0);
    }

    [Fact]
    public async Task ADryRun_SavesNothing_AndSaysWhatAWrongDocumentLacks()
    {
        await using var server = await StartAsync();
        var home = await HomeAsync(server);

        var good = await SeedAsync(server, home, _kitchen, dryRun: true);
        var bad = await SeedAsync(server, home, new JsonObject { ["spaces"] = new JsonArray() }, dryRun: true);

        good.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await good.Content.ReadFromJsonAsync<JsonObject>())!["saved"]!.GetValue<bool>().ShouldBeFalse();
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).ShouldContain("at least one space");
        (await SectionsAsync(server)).ShouldBe(0);
    }

    [Fact]
    public async Task ASectionTheHouseholdSaved_IsLeftAsItIs()
    {
        await using var server = await StartAsync();
        var home = await HomeAsync(server);
        (await SeedAsync(server, home, _kitchen, dryRun: false)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var again = await SeedAsync(server, home, HouseholdSections.SpacesSchema.Write(new SpacesSection()), dryRun: false);

        (await again.Content.ReadFromJsonAsync<JsonObject>())!["alreadySaved"]!.GetValue<bool>().ShouldBeTrue();
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.HouseholdSections.SingleAsync()).Document.ShouldContain("kitchen");
    }

    // The migration knows the deployment's zone but not what the household is called: a seeded
    // document is laid over the section as the household reads it now, so what it leaves out stays
    // the household's — the name its row carries, the features it did not name.
    [Fact]
    public async Task ASeededDocument_KeepsWhatItLeavesOut_AsTheHouseholdReadsIt()
    {
        await using var server = await StartAsync();
        var home = await HomeAsync(server);

        var settings = await SeedAsync(server, home, new JsonObject { ["timeZone"] = "America/New_York", ["language"] = null },
            dryRun: false, section: "settings");
        var features = await SeedAsync(server, home, new JsonObject { ["idealista"] = true }, dryRun: false, section: "features");

        settings.StatusCode.ShouldBe(HttpStatusCode.OK, await settings.Content.ReadAsStringAsync());
        features.StatusCode.ShouldBe(HttpStatusCode.OK, await features.Content.ReadAsStringAsync());
        var store = server.Service<global::Accounts.Configuration.HouseholdConfigStore>();
        var saved = (await store.ReadAsync(home, HouseholdSections.SettingsSchema, CancellationToken.None))!;
        saved.Name.ShouldBe(AccountsDatabase.HomeTenantName);
        saved.TimeZone.ShouldBe("America/New_York");
        var offered = (await store.ReadAsync(home, HouseholdSections.FeaturesSchema, CancellationToken.None))!;
        offered.ShouldBe(new FeaturesSection { Idealista = true });
    }

    // The home's bots are seeded as any secret is saved: sealed at rest, and answered unsealed to the
    // Telegram server alone.
    [Fact]
    public async Task ASeededSecret_IsSealedAtRest_AndAnsweredToItsServer()
    {
        await using var server = await StartAsync();
        var home = await HomeAsync(server);
        var bots = new JsonObject { ["telegramBots"] = new JsonObject { ["jack"] = new JsonObject { ["token"] = "1001:jack-token" } } };

        var seeded = await SeedAsync(server, home, bots, dryRun: false, section: "keys");

        seeded.StatusCode.ShouldBe(HttpStatusCode.OK, await seeded.Content.ReadAsStringAsync());
        await using (var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync())
        {
            var stored = (await db.HouseholdSections.SingleAsync()).Document;
            stored.ShouldContain("\"sealed\"");
            stored.ShouldNotContain("1001:jack-token");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, HouseholdConfigProtocol.PathOf(HouseholdConfigProtocol.TelegramBotsView))
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("Authorization", $"Bearer {TelegramCredential}");
        var answered = await (await server.Client().SendAsync(request)).Content.ReadFromJsonAsync<HouseholdTelegramBots>();
        answered!.Bots.ShouldBe([new HouseholdTelegramBot(home, "jack", "1001:jack-token")]);
    }

    // Keys are credentials, each its own: a household that saved some before the migration ran — the
    // operator setting Idealista's first — is given the ones it lacks and keeps every one it holds, a
    // bot by the agent it is bound to. Seeded again, nothing is left to give, and nothing is saved.
    [Fact]
    public async Task AKeysSectionTheHouseholdSaved_IsGivenOnlyTheKeysItLacks()
    {
        await using var server = await StartAsync();
        var home = await HomeAsync(server);
        var store = server.Service<HouseholdConfigStore>();
        var own = HouseholdSections.KeysSchema.Write(new KeysSection
        {
            Idealista = new IdealistaKeys("idealista-own", "secret-own"),
            TelegramBots = new Dictionary<string, TelegramBotKey> { ["jonas"] = new("2002:jonas-own") }
        });
        (await store.SaveAsync(home, HouseholdSections.KeysSchema, own, CancellationToken.None)).ShouldBeOfType<SectionSave.Saved>();
        var seeded = new JsonObject
        {
            ["idealista"] = new JsonObject { ["apiKey"] = "idealista-old", ["secret"] = "secret-old" },
            ["telegramBots"] = new JsonObject
            {
                ["jack"] = new JsonObject { ["token"] = "1001:jack-old" }, ["jonas"] = new JsonObject { ["token"] = "1002:jonas-old" }
            }
        };

        var given = await SeedAsync(server, home, seeded, dryRun: false, section: "keys");
        var stored = await StoredKeysAsync(server);
        var again = await SeedAsync(server, home, seeded, dryRun: false, section: "keys");

        given.StatusCode.ShouldBe(HttpStatusCode.OK, await given.Content.ReadAsStringAsync());
        var first = (await given.Content.ReadFromJsonAsync<JsonObject>())!;
        first["saved"]!.GetValue<bool>().ShouldBeTrue();
        first["kept"]!.AsArray().Select(k => k!.GetValue<string>()).ShouldBe(["idealista", "telegramBots/jonas"]);
        var keys = (await store.ReadAsync(home, HouseholdSections.KeysSchema, CancellationToken.None))!;
        keys.Idealista.ShouldBe(new IdealistaKeys("idealista-own", "secret-own"));
        keys.TelegramBots.Select(b => (b.Key, b.Value.Token))
            .ShouldBe([("jack", "1001:jack-old"), ("jonas", "2002:jonas-own")], ignoreOrder: true);
        (await again.Content.ReadFromJsonAsync<JsonObject>())!["alreadySaved"]!.GetValue<bool>().ShouldBeTrue();
        (await StoredKeysAsync(server)).ShouldBe(stored);
    }

    // A stack with no master key can seal nothing: a secret seeded there is refused, dry run or not,
    // and says why, rather than failing as a fault.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASecretSeededWithNoMasterKey_IsRefusedAndSavesNothing(bool dryRun)
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["household-migration"] = Credential },
            HouseholdConfig = new() { MasterKey = "" }
        });
        var bots = new JsonObject { ["telegramBots"] = new JsonObject { ["jack"] = new JsonObject { ["token"] = "1001:jack-token" } } };

        var refused = await SeedAsync(server, await HomeAsync(server), bots, dryRun, section: "keys");

        refused.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("no master key");
        (await SectionsAsync(server)).ShouldBe(0);
    }

    private static async Task<HttpResponseMessage> SeedAsync(
        AccountsServer server, string household, JsonObject document, bool dryRun, string credential = Credential,
        string section = "spaces")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Seed)
        {
            Content = JsonContent.Create(new { householdId = household, section, document, dryRun })
        };
        request.Headers.Add("Authorization", $"Bearer {credential}");
        return await server.Client().SendAsync(request);
    }

    private static async Task<string> HomeAsync(AccountsServer server)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        return await db.Tenants.Where(t => t.IsHome).Select(t => t.Id).SingleAsync();
    }

    // The stored keys as they stand, sealed: a save seals every secret afresh, so this changes with any save.
    private static async Task<string> StoredKeysAsync(AccountsServer server)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        return (await db.HouseholdSections.SingleAsync(s => s.Section == HouseholdSections.Keys)).Document;
    }

    private static async Task<int> SectionsAsync(AccountsServer server)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        return await db.HouseholdSections.CountAsync();
    }
}