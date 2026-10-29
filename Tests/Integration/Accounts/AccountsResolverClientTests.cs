using Accounts.Links;
using Accounts.Store;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The client every channel server asks the accounts service through, against the service itself:
// an identity nobody holds is nobody, which a channel refuses or treats as a guest, while a call the
// service could not answer is a failure the caller must not mistake for nobody.
public class AccountsResolverClientTests
{
    private const string Credential = "signalr-credential";

    [Fact]
    public async Task AnAccountsId_ResolvesToTheAccountAndItsName()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        var resolved = await ClientFor(server, Credential).ResolveAsync("signalr", fran.UserId, CancellationToken.None);

        resolved.ShouldBe(new ResolvedAccount(new Account(fran.TenantId, fran.UserId), "Fran"));
    }

    [Fact]
    public async Task AnIdentityNobodyHolds_IsNobody()
    {
        await using var server = await StartAsync();

        var resolved = await ClientFor(server, Credential).ResolveAsync("telegram", "123456789", CancellationToken.None);

        resolved.ShouldBeNull();
    }

    [Fact]
    public async Task ARefusedCredential_IsAFailureNotNobody()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        await Should.ThrowAsync<HttpRequestException>(
            () => ClientFor(server, "wrong").ResolveAsync("signalr", fran.UserId, CancellationToken.None));
    }

    // The Telegram bot's calls, through the same client: what a person sending the bot a code, or
    // asking for a web link, or an owner binding a group, is answered by.
    [Fact]
    public async Task AnInviteRedeemedInTelegram_IsThePersonItCreated_AndThenRefused()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var invite = await server.Service<LinkStore>().MintInviteAsync(fran.TenantId, fran.UserId, "Laura", Role.Member, CancellationToken.None);
        var client = ClientFor(server, Credential);

        var redeemed = await client.RedeemStartAsync(5_550_001, invite.Token, CancellationToken.None);

        redeemed.Outcome.ShouldBe(TelegramStartOutcome.Invited);
        redeemed.Account.ShouldNotBeNull().DisplayName.ShouldBe("Laura");
        redeemed.Account.Account.TenantId.ShouldBe(fran.TenantId);
        (await client.ResolveAsync("telegram", "5550001", CancellationToken.None)).ShouldBe(redeemed.Account);
        (await client.RedeemStartAsync(5_550_002, invite.Token, CancellationToken.None))
            .ShouldBe(new TelegramStart(TelegramStartOutcome.CodeRefused));
    }

    [Fact]
    public async Task ALinkCodeForATelegramSomebodyHolds_IsTaken()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var links = server.Service<LinkStore>();
        var client = ClientFor(server, Credential);
        var frans = await links.MintTelegramCodeAsync(fran.TenantId, fran.UserId, CancellationToken.None);
        (await client.RedeemStartAsync(5_550_001, frans.Code, CancellationToken.None)).Outcome.ShouldBe(TelegramStartOutcome.Linked);
        var lauras = await links.MintTelegramCodeAsync(fran.TenantId, laura, CancellationToken.None);

        var taken = await client.RedeemStartAsync(5_550_001, lauras.Code, CancellationToken.None);

        taken.ShouldBe(new TelegramStart(TelegramStartOutcome.TelegramTaken));
    }

    [Fact]
    public async Task AWebLink_IsMintedForALinkedPersonAndNobodyElse()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var client = ClientFor(server, Credential);
        var code = await server.Service<LinkStore>().MintTelegramCodeAsync(fran.TenantId, laura, CancellationToken.None);
        await client.RedeemStartAsync(5_550_001, code.Code, CancellationToken.None);

        var minted = await client.MintWebLinkAsync(5_550_001, CancellationToken.None);

        minted.Outcome.ShouldBe(TelegramWebLinkOutcome.Minted);
        minted.Url.ShouldNotBeNull().AbsolutePath.ShouldStartWith("/accounts/enroll/");
        minted.ExpiresAt.ShouldBe(server.Clock.GetUtcNow().AddDays(7));
        (await client.MintWebLinkAsync(5_550_002, CancellationToken.None))
            .ShouldBe(new TelegramWebLink(TelegramWebLinkOutcome.NotLinked));
    }

    [Fact]
    public async Task AGroupBoundByAnOwnersCode_IsTheHouseholds()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var client = ClientFor(server, Credential);
        var code = await server.Service<LinkStore>().MintGroupCodeAsync(fran.TenantId, fran.UserId, CancellationToken.None);
        (await client.GroupTenantAsync(-100_123, CancellationToken.None)).ShouldBeNull();

        var bound = await client.BindGroupAsync(-100_123, code.Code, CancellationToken.None);

        bound.ShouldBe(new TelegramBind(TelegramBindOutcome.Bound, fran.TenantId));
        (await client.GroupTenantAsync(-100_123, CancellationToken.None)).ShouldBe(fran.TenantId);
        (await client.BindGroupAsync(-100_456, code.Code, CancellationToken.None))
            .ShouldBe(new TelegramBind(TelegramBindOutcome.CodeRefused));
    }

    [Fact]
    public async Task ARefusedCredential_IsAFailureForTelegramsCallsToo()
    {
        await using var server = await StartAsync();
        var client = ClientFor(server, "wrong");

        await Should.ThrowAsync<HttpRequestException>(() => client.RedeemStartAsync(1, "code", CancellationToken.None));
        await Should.ThrowAsync<HttpRequestException>(() => client.MintWebLinkAsync(1, CancellationToken.None));
        await Should.ThrowAsync<HttpRequestException>(() => client.BindGroupAsync(-1, "code", CancellationToken.None));
        await Should.ThrowAsync<HttpRequestException>(() => client.GroupTenantAsync(-1, CancellationToken.None));
    }

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-channel-signalr"] = Credential }
        });

    private static AccountsResolverClient ClientFor(AccountsServer server, string credential) =>
        new(new HttpClient { BaseAddress = server.Address }, credential);
}