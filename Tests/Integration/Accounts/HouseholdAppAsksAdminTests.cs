using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Settings;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Clients.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household's daily caps on its apps' asks through the admin API (seam 4, the owner admin): an owner sets
// the household's default and a cap of an app's own, for one of the household's published apps alone, saved
// through the app asks section's own save. The apps server's side is the app asks view it reads on a viewer's
// `apps` assertion, which hears the save.
public sealed class HouseholdAppAsksAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AppsCredential = "apps-credential";

    [Fact]
    public async Task AnOwner_SetsTheHouseholdsCapAndAnAppsOwn_WhichTheAppsServerHears()
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var id = await PublishedAsync(apps, new Account(household, ana), "glucose");
        await using var reader = AppsReader(server);
        var proof = HouseholdProof.OfAssertion(server.Service<global::Accounts.Signing.Assertions>().OfGuest(household, AppChannels.Apps));
        (await reader.AppAsksAsync(household, proof, CancellationToken.None)).LimitOf(id, 200).ShouldBe(200);
        var admin = server.Admin(await server.MintSessionAsync(ana));
        var loaded = await CapsAsync(admin);

        var saved = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(new AppAsksSection { DailyCap = 50, Apps = new Dictionary<string, int> { [id] = 5 } }, loaded.Caps));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var answer = (await saved.Content.ReadFromJsonAsync<AdminSaved<AdminAppAsks>>())!;
        answer.Value.Caps.DailyCap.ShouldBe(50);
        answer.Value.Caps.Apps.ShouldBe(new Dictionary<string, int> { [id] = 5 });
        var app = answer.Value.Apps.ShouldNotBeNull().ShouldHaveSingleItem();
        (app.Id, app.Name, app.AuthorName).ShouldBe((id, "Glucose today", "Ana"));
        answer.Warning.ShouldBeNull();
        loaded.Caps.DailyCap.ShouldBeNull();
        loaded.Apps.ShouldNotBeNull().Select(a => a.Id).ShouldBe([id]);
        // The reader keeps what it read for longer than the test runs: only the save's announcement reads it again.
        await Eventually.Until(async () => (await reader.AppAsksAsync(household, proof, CancellationToken.None)).LimitOf(id, 200) == 5,
            "the announced save to reach the apps server's reader");
        (await reader.AppAsksAsync(household, proof, CancellationToken.None)).LimitOf("d4e5f6", 200).ShouldBe(50);
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe([$"Set the household's daily cap; set app {id}'s daily cap"]);
    }

    [Fact]
    public async Task AMember_IsRefusedTheCaps_AndChangesNothing()
    {
        await using var server = await StartAsync();
        var (household, ana) = await AppsHouseholdAsync(server);
        var bea = server.Admin(await server.MintSessionAsync(await server.SeedPersonAsync(household, "Bea")));

        var read = await bea.GetAsync(AdminProtocol.AppAsks);
        var refused = await bea.PutAsync(AdminProtocol.AppAsks, new AppAsksSection { DailyCap = 1 });

        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CapsAsync(server.Admin(await server.MintSessionAsync(ana)))).Caps.DailyCap.ShouldBeNull();
    }

    // No cap is below none, nor above the section's ceiling: refused in the section's own words, nothing saved.
    [Theory]
    [InlineData(-1, "household's daily cap")]
    [InlineData(AppAsksSection.MaxDailyCap + 1, "household's daily cap")]
    public async Task ACapOutsideItsBounds_IsRefused_AndNothingChanges(int cap, string said)
    {
        await using var server = await StartAsync();
        var (_, ana) = await AppsHouseholdAsync(server);
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.AppAsks, new AppAsksSection { DailyCap = cap });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem().ShouldContain(said);
        (await CapsAsync(admin)).Caps.DailyCap.ShouldBeNull();
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    // A cap of an app's own is only for one of the household's published apps: one not published yet, another
    // household's, or no app at all is refused naming it, and nothing is saved.
    [Theory]
    [InlineData("unpublished")]
    [InlineData("elsewhere")]
    [InlineData("unknown")]
    public async Task ACapForNoneOfTheHouseholdsPublishedApps_IsRefused(string which)
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var (elsewhere, stranger) = await server.SeedHouseholdAsync("Lejos", "Stranger");
        var id = which switch
        {
            "unpublished" => AppsServer.IdIn(await apps.AppAsync(new Account(household, ana), "draft")),
            "elsewhere" => await PublishedAsync(apps, new Account(elsewhere, stranger), "theirs"),
            _ => "zzzzzz"
        };
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.AppAsks, new AppAsksSection { Apps = new Dictionary<string, int> { [id] = 3 } });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem().ShouldContain($"App {id}");
        (await CapsAsync(admin)).Caps.Apps.ShouldBeEmpty();
    }

    // The apps server not answering costs only an app's cap set or changed: the caps still load (with no apps
    // listed), the household's default still saves, an app's cap left as loaded or removed still saves.
    [Fact]
    public async Task AnAppsServerThatCannotBeAsked_StopsOnlyAnAppsCapSetOrChanged()
    {
        await using var server = await StartAsync();
        var (household, ana) = await AppsHouseholdAsync(server);
        await server.Service<HouseholdConfigStore>().SaveAsync(household, HouseholdSections.AppAsksSchema,
            HouseholdSections.AppAsksSchema.Write(new AppAsksSection { Apps = new Dictionary<string, int> { ["a1b2c3"] = 4, ["d4e5f6"] = 6 } }),
            CancellationToken.None);
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var loaded = await CapsAsync(admin);
        var defaulted = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(loaded.Caps with { DailyCap = 20 }, loaded.Caps));
        var removed = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(loaded.Caps with { Apps = new Dictionary<string, int> { ["a1b2c3"] = 4 } }, loaded.Caps));
        var changed = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(loaded.Caps with { Apps = new Dictionary<string, int> { ["a1b2c3"] = 9 } }, loaded.Caps));

        loaded.Apps.ShouldBeNull();
        defaulted.StatusCode.ShouldBe(HttpStatusCode.OK, await defaulted.Content.ReadAsStringAsync());
        removed.StatusCode.ShouldBe(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());
        changed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await changed.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldBe(AdminSpaces.AppsUnavailableMessage);
        var caps = (await CapsAsync(admin)).Caps;
        caps.DailyCap.ShouldBe(20);
        caps.Apps.ShouldBe(new Dictionary<string, int> { ["a1b2c3"] = 4 });
    }

    // Two owners with the page open together: one sets an app's cap and removes another's, the other sets a
    // third app's and the household's default, and every change stands; both changing one app's cap to
    // different values is refused naming it, nothing saved.
    [Fact]
    public async Task TwoOwnersCappingDifferentApps_BothStand_ButOneAppCappedTwoWaysIsRefused()
    {
        await using var apps = await AppsAsync();
        await using var server = await StartAsync(apps);
        var (household, ana) = await AppsHouseholdAsync(server);
        var author = new Account(household, ana);
        var glucose = await PublishedAsync(apps, author, "glucose");
        var carbs = await PublishedAsync(apps, author, "carbs");
        var lights = await PublishedAsync(apps, author, "lights");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        (await admin.PutAsync(AdminProtocol.AppAsks, new AppAsksSection { Apps = new Dictionary<string, int> { [lights] = 2 } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var loaded = (await CapsAsync(admin)).Caps;

        var mine = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(loaded with { Apps = new Dictionary<string, int> { [glucose] = 5 } }, loaded));
        var theirs = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(loaded with { DailyCap = 30, Apps = new Dictionary<string, int> { [lights] = 2, [carbs] = 7 } }, loaded));
        var again = (await CapsAsync(admin)).Caps;
        var first = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(again with { Apps = new Dictionary<string, int>(again.Apps) { [carbs] = 8 } }, again));
        var second = await admin.PutAsync(AdminProtocol.AppAsks,
            AdminEdit.Of(again with { Apps = new Dictionary<string, int>(again.Apps) { [carbs] = 9 } }, again));

        mine.StatusCode.ShouldBe(HttpStatusCode.OK, await mine.Content.ReadAsStringAsync());
        theirs.StatusCode.ShouldBe(HttpStatusCode.OK, await theirs.Content.ReadAsStringAsync());
        again.DailyCap.ShouldBe(30);
        again.Apps.ShouldBe(new Dictionary<string, int> { [glucose] = 5, [carbs] = 7 }, ignoreOrder: true);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain($"app {carbs}'s daily cap");
        (await CapsAsync(admin)).Caps.Apps[carbs].ShouldBe(8);
    }

    // The apps server, verifying this service's assertions on a clock that agrees with it.
    private static Task<AppsServer> AppsAsync() =>
        AppsServer.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(AccountsServer.StartedAt))));

    private Task<AccountsServer> StartAsync(AppsServer apps) => StartAsync($"http://localhost:{apps.Settings.Listeners.Mcp}");

    // appsUrl: where the apps server listens; none is one that would never be asked.
    private Task<AccountsServer> StartAsync(string? appsUrl = null) =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-apps"] = AppsCredential },
            Redis = new() { ConnectionString = redis.ConnectionString },
            // The suite's accounts key, which the apps server's verifier holds (TestAssertions).
            Signing = new SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
            AppsUrl = appsUrl ?? DeadEnd.Url()
        });

    // A household the operator gave the apps feature, and its owner.
    private static async Task<(string Household, string Owner)> AppsHouseholdAsync(AccountsServer server)
    {
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await server.Service<HouseholdConfigStore>().SaveAsync(
            household, HouseholdSections.FeaturesSchema, new JsonObject { ["apps"] = true }, CancellationToken.None);
        return (household, ana);
    }

    private static async Task<string> PublishedAsync(AppsServer apps, Account author, string slug)
    {
        var id = AppsServer.IdIn(await apps.AppAsync(author, slug, slug == "glucose" ? "Glucose today" : slug));
        await apps.CreateAsync(author, $"/{slug}/index.html", "<h1>Hi</h1>");
        await apps.ExecAsync(author, $"/{slug}", "./publish");
        return id;
    }

    // The apps server's own reader, keeping what it read for longer than any test runs.
    private HouseholdConfigurationClient AppsReader(AccountsServer server) =>
        new(server.Client(), AppsCredential, null, redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));

    private static async Task<AdminAppAsks> CapsAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.AppAsks)).Content.ReadFromJsonAsync<AdminAppAsks>())!;

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
}