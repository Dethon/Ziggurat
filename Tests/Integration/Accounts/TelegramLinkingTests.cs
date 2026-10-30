using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accounts.Store;
using Domain.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// What the Telegram bot asks the accounts service, with its own credential: a start parameter
// redeemed (an invite, which creates the person, or the account page's link code, which links
// them), recorded against the numeric id the bot saw; a passkey link for a linked person; a group
// bound to a household by an owner's code. Each is the internal API's, never a browser's.
public partial class TelegramLinkingTests
{
    private const string Credential = "telegram-credential";
    private const string Members = "/accounts/members";
    private const string Account = "/accounts/account";
    private const long LauraTelegram = 5_550_001;

    // A Telegram group's id is negative; a supergroup's starts -100.
    private const long FamilyGroup = -100_123_456;

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-channel-telegram"] = Credential }
        });

    [Fact]
    public async Task AnInviteRedeemedThroughTheStartParameter_CreatesThePersonLinkedToTheirTelegram()
    {
        await using var server = await StartAsync();
        var owner = await OwnerAsync(server);
        var invite = await InviteAsync(owner, "Laura", "member");

        var response = await StartWithAsync(server, LauraTelegram, invite);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var redeemed = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        redeemed["outcome"]!.GetValue<string>().ShouldBe("invited");
        redeemed["displayName"]!.GetValue<string>().ShouldBe("Laura");
        var resolved = await ResolveAsync(server, LauraTelegram);
        resolved["userId"]!.GetValue<string>().ShouldBe(redeemed["userId"]!.GetValue<string>());
        resolved["tenantId"]!.GetValue<string>().ShouldBe(redeemed["tenantId"]!.GetValue<string>());
        resolved["displayName"]!.GetValue<string>().ShouldBe("Laura");
        (await owner.PageAsync(Members)).ShouldContain(">Laura<");
    }

    [Fact]
    public async Task TheAccountPagesCode_LinksItsPersonToTheTelegramThatRedeemsIt()
    {
        await using var server = await StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var code = await TelegramCodeAsync(server, laura);

        var response = await StartWithAsync(server, LauraTelegram, code);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var linked = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        linked["outcome"]!.GetValue<string>().ShouldBe("linked");
        linked["userId"]!.GetValue<string>().ShouldBe(laura);
        (await ResolveAsync(server, LauraTelegram))["userId"]!.GetValue<string>().ShouldBe(laura);
    }

    [Fact]
    public async Task ACodeRedeemedOnce_IsRefusedTheSecondTime()
    {
        await using var server = await StartAsync();
        var invite = await InviteAsync(await OwnerAsync(server), "Laura", "member");
        (await StartWithAsync(server, LauraTelegram, invite)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var again = await StartWithAsync(server, 5_550_002, invite);

        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ResolveStatusAsync(server, 5_550_002)).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnInvitePastItsWeek_IsRefusedAndCreatesNobody()
    {
        await using var server = await StartAsync();
        var owner = await OwnerAsync(server);
        var invite = await InviteAsync(owner, "Laura", "member");
        server.Clock.Advance(TimeSpan.FromDays(7));

        var response = await StartWithAsync(server, LauraTelegram, invite);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.PageAsync(Members)).ShouldNotContain(">Laura<");
    }

    // The start parameter redeems an invite or the account page's code and nothing else: a recovery
    // link adds a passkey, and a group's code binds a group.
    [Fact]
    public async Task ARecoveryLinkOrAGroupsCode_IsNoStartParameter()
    {
        await using var server = await StartAsync();
        var owner = await OwnerAsync(server);
        var laura = await server.SeedMemberAsync("Laura");
        var recovery = EnrollLink().Match(await (await owner.PostAsync(Members, $"{Members}/{laura}/recovery"))
            .Content.ReadAsStringAsync()).Groups["token"].Value;
        var group = await GroupCodeAsync(owner);

        (await StartWithAsync(server, LauraTelegram, recovery)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await StartWithAsync(server, LauraTelegram, group)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ResolveStatusAsync(server, LauraTelegram)).ShouldBe(HttpStatusCode.NotFound);
    }

    // A Telegram is one person's: the code that would have made it somebody else's stays open for
    // the Telegram it was meant for.
    [Fact]
    public async Task ATelegramSomebodyElseHolds_IsRefusedAndTheCodeStaysOpen()
    {
        await using var server = await StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var pablo = await server.SeedMemberAsync("Pablo");
        (await StartWithAsync(server, LauraTelegram, await TelegramCodeAsync(server, laura))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var pablos = await TelegramCodeAsync(server, pablo);

        (await StartWithAsync(server, LauraTelegram, pablos)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await ResolveAsync(server, LauraTelegram))["userId"]!.GetValue<string>().ShouldBe(laura);
        (await StartWithAsync(server, 5_550_002, pablos)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // An invite makes somebody new, so a Telegram that is anyone's already would be two people.
    [Fact]
    public async Task AnInviteFromATelegramAlreadyLinked_IsRefusedAndStaysOpen()
    {
        await using var server = await StartAsync();
        var owner = await OwnerAsync(server);
        var laura = await server.SeedMemberAsync("Laura");
        (await StartWithAsync(server, LauraTelegram, await TelegramCodeAsync(server, laura))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var invite = await InviteAsync(owner, "Pablo", "member");

        (await StartWithAsync(server, LauraTelegram, invite)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await owner.PageAsync(Members)).ShouldNotContain(">Pablo<");
        (await StartWithAsync(server, 5_550_002, invite)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // A person holds one Telegram: linking a new one, from a new phone number, lets go of the old.
    [Fact]
    public async Task LinkingAnotherTelegram_ReplacesThePersonsFirst()
    {
        await using var server = await StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        await StartWithAsync(server, LauraTelegram, await TelegramCodeAsync(server, laura));

        (await StartWithAsync(server, 5_550_009, await TelegramCodeAsync(server, laura))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ResolveStatusAsync(server, LauraTelegram)).ShouldBe(HttpStatusCode.NotFound);
        (await ResolveAsync(server, 5_550_009))["userId"]!.GetValue<string>().ShouldBe(laura);
    }

    // Somebody who joined through Telegram asks the bot for a way onto the web: a link that adds a
    // passkey to the account their Telegram is linked to, and to nobody else's.
    [Fact]
    public async Task AWebLink_AddsAPasskeyToTheLinkedPerson()
    {
        await using var server = await StartAsync();
        var invite = await InviteAsync(await OwnerAsync(server), "Laura", "member");
        await StartWithAsync(server, LauraTelegram, invite);

        var response = await PostAsync(server, "/accounts/internal/telegram/web-link", new { telegramId = LauraTelegram });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var page = await server.Client().GetAsync(link["url"]!.GetValue<string>());
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<strong>Laura</strong>");
        link["expiresAt"]!.GetValue<DateTimeOffset>().ShouldBe(server.Clock.GetUtcNow().AddDays(7));
    }

    [Fact]
    public async Task AWebLink_IsNobodysForATelegramNobodyLinked()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");

        var response = await PostAsync(server, "/accounts/internal/telegram/web-link", new { telegramId = LauraTelegram });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The operator's way to a new passkey is their account page or the container's command, as the
    // members page already says: their Telegram is not to be a way onto the dashboard.
    [Fact]
    public async Task AWebLink_IsRefusedTheOperator()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        await StartWithAsync(server, 5_550_100, await TelegramCodeAsync(server, fran.UserId));

        var response = await PostAsync(server, "/accounts/internal/telegram/web-link", new { telegramId = 5_550_100L });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnOwnersGroupCode_BindsTheGroupToTheHousehold()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var owner = server.SignedIn(await server.MintSessionAsync(fran.UserId));
        (await GroupTenantStatusAsync(server, FamilyGroup)).ShouldBe(HttpStatusCode.NotFound);

        var response = await BindAsync(server, FamilyGroup, await GroupCodeAsync(owner));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["tenantId"]!.GetValue<string>().ShouldBe(fran.TenantId);
        var group = await PostAsync(server, "/accounts/internal/telegram/group", new { chatId = FamilyGroup });
        group.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await group.Content.ReadFromJsonAsync<JsonObject>())!["tenantId"]!.GetValue<string>().ShouldBe(fran.TenantId);
    }

    [Fact]
    public async Task AGroupCodeUsedOnce_BindsNoOtherGroup()
    {
        await using var server = await StartAsync();
        var code = await GroupCodeAsync(await OwnerAsync(server));
        (await BindAsync(server, FamilyGroup, code)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await BindAsync(server, -100_999, code)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await GroupTenantStatusAsync(server, -100_999)).ShouldBe(HttpStatusCode.NotFound);
    }

    // The start parameter's codes link a person; they bind no group.
    [Fact]
    public async Task AnInviteOrALinkCode_BindsNoGroup()
    {
        await using var server = await StartAsync();
        var owner = await OwnerAsync(server);
        var laura = await server.SeedMemberAsync("Laura");

        (await BindAsync(server, FamilyGroup, await InviteAsync(owner, "Pablo", "member"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BindAsync(server, FamilyGroup, await TelegramCodeAsync(server, laura))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GroupTenantStatusAsync(server, FamilyGroup)).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AMember_GetsNoGroupCode()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.SignedIn(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await laura.PostAsync(Account, $"{Members}/telegram-group")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // A group is one household's: another household's code cannot take it over.
    [Fact]
    public async Task AGroupBoundToAnotherHousehold_IsNotRebound()
    {
        await using var server = await StartAsync();
        var owner = await OwnerAsync(server);
        var other = await SeedHouseholdAsync(server);
        (await BindAsync(server, FamilyGroup, await GroupCodeAsync(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var theirs = await server.SignedIn(await server.MintSessionAsync(other.OwnerId)).PostAsync(Account, $"{Members}/telegram-group");
        var theirCode = BindCommand().Match(await theirs.Content.ReadAsStringAsync()).Groups["code"].Value;

        (await BindAsync(server, FamilyGroup, theirCode)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var group = await PostAsync(server, "/accounts/internal/telegram/group", new { chatId = FamilyGroup });
        (await group.Content.ReadFromJsonAsync<JsonObject>())!["tenantId"]!.GetValue<string>().ShouldNotBe(other.TenantId);
    }

    // An invite is redeemable in Telegram as well as in a browser, so the members page hands out
    // both: the enrollment link, and the bot's deep link carrying the same token.
    [Fact]
    public async Task AnInvite_IsAlsoTheBotsDeepLink()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-channel-telegram"] = Credential },
            Telegram = new() { BotUsername = "ziggurat_bot" }
        });
        var owner = await OwnerAsync(server);

        var page = await (await owner.PostAsync(Members, $"{Members}/invite", ("displayName", "Laura"), ("role", "member")))
            .Content.ReadAsStringAsync();

        var token = EnrollLink().Match(page).Groups["token"].Value;
        page.ShouldContain($"""<a data-telegram-link href="https://t.me/ziggurat_bot?start={token}">""");
        (await StartWithAsync(server, LauraTelegram, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // A question a person's turn asks is offered to them in their Telegram private chat, whose id
    // is their own Telegram id: the bot asks whose it is by the account, within its household.
    [Fact]
    public async Task APersonsTelegram_IsFoundByTheirAccount_AndNobodysForSomeoneUnlinked()
    {
        await using var server = await StartAsync();
        var laura = await server.SeedMemberAsync("Laura");
        var marta = await server.SeedMemberAsync("Marta");
        await StartWithAsync(server, LauraTelegram, await TelegramCodeAsync(server, laura));
        var tenantId = (await ResolveAsync(server, LauraTelegram))["tenantId"]!.GetValue<string>();

        var found = await PostAsync(server, "/accounts/internal/telegram/of-account", new { tenantId, userId = laura });

        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await found.Content.ReadFromJsonAsync<JsonObject>())!["telegramId"]!.GetValue<long>().ShouldBe(LauraTelegram);
        (await PostAsync(server, "/accounts/internal/telegram/of-account", new { tenantId, userId = marta }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(server, "/accounts/internal/telegram/of-account", new { tenantId = "elsewhere", userId = laura }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/accounts/internal/telegram/of-account", null)]
    [InlineData("/accounts/internal/telegram/start", null)]
    [InlineData("/accounts/internal/telegram/start", "not-a-credential")]
    [InlineData("/accounts/internal/telegram/web-link", null)]
    [InlineData("/accounts/internal/telegram/bind", "not-a-credential")]
    [InlineData("/accounts/internal/telegram/group", null)]
    public async Task ACallerWithoutAConfiguredCredential_Is401(string path, string? credential)
    {
        await using var server = await StartAsync();
        var invite = await InviteAsync(await OwnerAsync(server), "Laura", "member");

        var response = await PostAsync(server, path,
            new { telegramId = LauraTelegram, chatId = -100_123L, code = invite }, credential);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await StartWithAsync(server, LauraTelegram, invite)).StatusCode.ShouldBe(HttpStatusCode.OK, "nothing was spent");
    }

    private static async Task<AccountsServer.PageClient> OwnerAsync(AccountsServer server)
    {
        var fran = await server.MintOperatorAsync("Fran");
        return server.SignedIn(await server.MintSessionAsync(fran.UserId));
    }

    // The invite's token, which is what its Telegram deep link carries as the start parameter.
    private static async Task<string> InviteAsync(AccountsServer.PageClient owner, string displayName, string role)
    {
        var response = await owner.PostAsync(Members, $"{Members}/invite", ("displayName", displayName), ("role", role));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = EnrollLink().Match(await response.Content.ReadAsStringAsync());
        link.Success.ShouldBeTrue();
        return link.Groups["token"].Value;
    }

    private static async Task<string> TelegramCodeAsync(AccountsServer server, string userId)
    {
        var response = await server.SignedIn(await server.MintSessionAsync(userId)).PostAsync(Account, $"{Account}/telegram");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = TelegramCode().Match(await response.Content.ReadAsStringAsync());
        code.Success.ShouldBeTrue();
        return code.Groups["code"].Value;
    }

    private static async Task<string> GroupCodeAsync(AccountsServer.PageClient owner)
    {
        var response = await owner.PostAsync(Members, $"{Members}/telegram-group");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = BindCommand().Match(await response.Content.ReadAsStringAsync());
        code.Success.ShouldBeTrue();
        return code.Groups["code"].Value;
    }

    private static Task<HttpResponseMessage> StartWithAsync(AccountsServer server, long telegramId, string code) =>
        PostAsync(server, "/accounts/internal/telegram/start", new { telegramId, code });

    private static Task<HttpResponseMessage> BindAsync(AccountsServer server, long chatId, string code) =>
        PostAsync(server, "/accounts/internal/telegram/bind", new { chatId, code });

    private static async Task<HttpStatusCode> GroupTenantStatusAsync(AccountsServer server, long chatId) =>
        (await PostAsync(server, "/accounts/internal/telegram/group", new { chatId })).StatusCode;

    // Only the home household exists in this phase, so a second one is written straight into the
    // store, with an owner of its own.
    private static async Task<(string TenantId, string OwnerId)> SeedHouseholdAsync(AccountsServer server)
    {
        await using var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        var tenant = new Tenant { Id = Tokens.NewId(), Name = "Elsewhere", CreatedAt = server.Clock.GetUtcNow() };
        var owner = new User
        {
            Id = Tokens.NewId(), TenantId = tenant.Id, DisplayName = "Marta", Role = Role.Owner, CreatedAt = server.Clock.GetUtcNow()
        };
        db.Tenants.Add(tenant);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        return (tenant.Id, owner.Id);
    }

    private static async Task<HttpStatusCode> ResolveStatusAsync(AccountsServer server, long telegramId) =>
        (await PostAsync(server, "/accounts/internal/resolve", new { channel = "telegram", identity = $"{telegramId}" })).StatusCode;

    private static async Task<JsonObject> ResolveAsync(AccountsServer server, long telegramId)
    {
        var response = await PostAsync(server, "/accounts/internal/resolve", new { channel = "telegram", identity = $"{telegramId}" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static Task<HttpResponseMessage> PostAsync(
        AccountsServer server, string path, object body, string? credential = Credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        }

        return server.Client().SendAsync(request);
    }

    [GeneratedRegex("""<a data-link href="[^"]+/enroll/(?<token>[A-Za-z0-9_-]+)">""")]
    private static partial Regex EnrollLink();

    [GeneratedRegex("<code data-telegram-code>(?<code>[A-Za-z0-9_-]+)</code>")]
    private static partial Regex TelegramCode();

    [GeneratedRegex("<code data-bind-command>/bind (?<code>[A-Za-z0-9_-]+)</code>")]
    private static partial Regex BindCommand();
}