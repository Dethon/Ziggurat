using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// What the household migration asks the accounts service, with the credential only it holds: the
// identity map's people made accounts of the home household, each found again by the name the map
// knew them by, so a second run makes nobody twice.
public partial class HouseholdImportTests
{
    private const string Credential = "migration-credential";
    private const string TelegramCredential = "telegram-credential";
    private const string Import = "/accounts/internal/household-import";
    private const long FranTelegram = 5_550_101;
    private const long AnaTelegram = 5_550_102;

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["household-migration"] = Credential, ["mcp-channel-telegram"] = TelegramCredential
            }
        });

    private static object Fran(long? telegramId = FranTelegram) =>
        new { key = "Fran", displayName = "Fran", telegramId, role = "owner", @operator = true };

    private static object Ana(long? telegramId = AnaTelegram) =>
        new { key = "Ana", displayName = "Ana", telegramId, role = "member", @operator = false };

    [Fact]
    public async Task TheIdentityMapsPeople_BecomeAccountsOfTheHomeHouseholdWithTheirTelegramLinked()
    {
        await using var server = await StartAsync();

        var imported = await ImportAsync(server, Fran(), Ana());

        var fran = Person(imported, "Fran");
        var ana = Person(imported, "Ana");
        fran["created"]!.GetValue<bool>().ShouldBeTrue();
        fran["role"]!.GetValue<string>().ShouldBe("owner");
        fran["operator"]!.GetValue<bool>().ShouldBeTrue();
        ana["role"]!.GetValue<string>().ShouldBe("member");
        ana["operator"]!.GetValue<bool>().ShouldBeFalse();
        (await ResolveAsync(server, FranTelegram)).ShouldBe(fran["userId"]!.GetValue<string>());
        (await ResolveAsync(server, AnaTelegram)).ShouldBe(ana["userId"]!.GetValue<string>());
        var enroll = await server.Client().GetAsync(new Uri(ana["link"]!.GetValue<string>()).PathAndQuery);
        enroll.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await enroll.Content.ReadAsStringAsync()).ShouldContain("Ana");
    }

    [Fact]
    public async Task ImportingThePeopleAgain_ChangesNobodyAndMintsNoLink()
    {
        await using var server = await StartAsync();
        var first = await ImportAsync(server, Fran(), Ana());

        var second = await ImportAsync(server, Fran(), Ana());

        foreach (var key in new[] { "Fran", "Ana" })
        {
            var again = Person(second, key);
            again["userId"]!.GetValue<string>().ShouldBe(Person(first, key)["userId"]!.GetValue<string>());
            again["created"]!.GetValue<bool>().ShouldBeFalse();
            again["changes"]!.AsArray().ShouldBeEmpty();
            again["link"].ShouldBeNull();
        }

        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.Users.CountAsync()).ShouldBe(2);
        (await db.Links.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task TheOperatorWhoMintedTheirOwnLinkFirst_IsTheOneImported()
    {
        await using var server = await StartAsync();
        var operatorLink = await server.MintOperatorAsync("Francisco");

        var imported = await ImportAsync(server, Fran(), Ana());

        var fran = Person(imported, "Fran");
        fran["userId"]!.GetValue<string>().ShouldBe(operatorLink.UserId);
        fran["created"]!.GetValue<bool>().ShouldBeFalse();
        fran["displayName"]!.GetValue<string>().ShouldBe("Francisco");
        fran["link"].ShouldBeNull();
        (await ResolveAsync(server, FranTelegram)).ShouldBe(operatorLink.UserId);
    }

    [Fact]
    public async Task SomebodyWhoLinkedTheirTelegramFirst_IsTheOneImportedWithThatTelegram()
    {
        await using var server = await StartAsync();
        var ana = await server.SeedMemberAsync("Ana");
        var code = await TelegramCodeAsync(server, ana);
        (await PostAsync(server, "/accounts/internal/telegram/start", new { telegramId = AnaTelegram, code }, TelegramCredential))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var imported = await ImportAsync(server, Ana());

        Person(imported, "Ana")["userId"]!.GetValue<string>().ShouldBe(ana);
        Person(imported, "Ana")["created"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task ATelegramSomebodyElseHolds_RefusesTheWholeImport()
    {
        await using var server = await StartAsync();
        await ImportAsync(server, Ana());

        var response = await PostAsync(server, Import, new
        {
            people = new[] { Fran(telegramId: null), new { key = "Bea", displayName = "Bea", telegramId = (long?)AnaTelegram, role = "member", @operator = false } },
            dryRun = false
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Ana");
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.Users.Select(u => u.DisplayName).ToListAsync()).ShouldBe(["Ana"]);
    }

    [Theory]
    [InlineData(TelegramCredential)]
    [InlineData("not-a-credential")]
    [InlineData(null)]
    public async Task AnyoneButTheMigration_IsRefusedBeforeAnybodyIsMade(string? credential)
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, Import, new { people = new[] { Fran() }, dryRun = false }, credential);

        response.IsSuccessStatusCode.ShouldBeFalse();
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.Users.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ADryRun_SaysWhatItWouldDoAndWritesNothing()
    {
        await using var server = await StartAsync();

        var response = await PostAsync(server, Import, new { people = new[] { Fran(), Ana() }, dryRun = true });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var planned = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Person(planned, "Ana")["created"]!.GetValue<bool>().ShouldBeTrue();
        Person(planned, "Ana")["userId"].ShouldBeNull();
        Person(planned, "Ana")["link"].ShouldBeNull();
        (await ResolveAsync(server, AnaTelegram)).ShouldBeNull();
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        (await db.Users.CountAsync()).ShouldBe(0);
    }

    private static async Task<JsonObject> ImportAsync(AccountsServer server, params object[] people)
    {
        var response = await PostAsync(server, Import, new { people, dryRun = false });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<string> TelegramCodeAsync(AccountsServer server, string userId)
    {
        const string account = "/accounts/account";
        var response = await server.SignedIn(await server.MintSessionAsync(userId)).PostAsync(account, $"{account}/telegram");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = TelegramCode().Match(await response.Content.ReadAsStringAsync());
        code.Success.ShouldBeTrue();
        return code.Groups["code"].Value;
    }

    private static JsonObject Person(JsonObject imported, string key) =>
        imported["people"]!.AsArray().Select(p => p!.AsObject()).Single(p => p["key"]!.GetValue<string>() == key);

    private static async Task<string?> ResolveAsync(AccountsServer server, long telegramId)
    {
        var response = await PostAsync(
            server, "/accounts/internal/resolve", new { channel = "telegram", identity = $"{telegramId}" },
            TelegramCredential);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : (await response.Content.ReadFromJsonAsync<JsonObject>())!["userId"]!.GetValue<string>();
    }

    private static Task<HttpResponseMessage> PostAsync(
        AccountsServer server, string path, object body, string? credential = Credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (credential is not null)
        {
            request.Headers.Add("Authorization", $"Bearer {credential}");
        }

        return server.Client().SendAsync(request);
    }

    [GeneratedRegex("<code data-telegram-code>(?<code>[A-Za-z0-9_-]+)</code>")]
    private static partial Regex TelegramCode();
}