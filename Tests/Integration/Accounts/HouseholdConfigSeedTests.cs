using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
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

    private static async Task<HttpResponseMessage> SeedAsync(
        AccountsServer server, string household, JsonObject document, bool dryRun, string credential = Credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Seed)
        {
            Content = JsonContent.Create(new { householdId = household, section = "spaces", document, dryRun })
        };
        request.Headers.Add("Authorization", $"Bearer {credential}");
        return await server.Client().SendAsync(request);
    }

    private static async Task<string> HomeAsync(AccountsServer server)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        return await db.Tenants.Where(t => t.IsHome).Select(t => t.Id).SingleAsync();
    }

    private static async Task<int> SectionsAsync(AccountsServer server)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        return await db.HouseholdSections.CountAsync();
    }
}