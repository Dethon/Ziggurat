using Accounts.Links;
using Accounts.Store;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Telegram.Bot.Types;
using Tests.Integration.Fixtures;
using Tests.Unit.McpChannelTelegram;

namespace Tests.Integration.Accounts;

// The bot against the accounts service itself, through the client the deployment uses: what an
// owner hands out on the members page and a person's account page is what lets somebody's Telegram
// in, and the turn the bot hands the agent acts for the account the service made or linked.
public class TelegramGateTests
{
    private const string Credential = "telegram-credential";
    private const long Carol = 7_770_001;

    [Fact]
    public async Task AnInviteRedeemedInTelegram_MakesTheSendersTurnsTheNewAccounts()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var invite = await server.Service<LinkStore>().MintInviteAsync(fran.TenantId, fran.UserId, "Carol", Role.Member, CancellationToken.None);
        using var harness = new TelegramPollingHarness(ClientFor(server));
        await harness.ReceiveAsync();
        harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage($"/start {invite.Token}", username: "carol", userId: Carol)),
            Update(TelegramPollingHarness.TextMessage("/what is on today", username: "carol", userId: Carol)));

        await harness.RunAsync();

        harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("Welcome, Carol");
        var principal = (await harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Principal.ShouldNotBeNull();
        var carol = (await ClientFor(server).ResolveAsync("telegram", $"{Carol}", CancellationToken.None)).ShouldNotBeNull();
        principal.ShouldBe(new Principal("telegram", "carol", carol.Account));
        carol.Account.TenantId.ShouldBe(fran.TenantId);
    }

    [Fact]
    public async Task AWebLink_OpensTheLinkedPersonsEnrollment()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var code = await server.Service<LinkStore>().MintTelegramCodeAsync(fran.TenantId, laura, CancellationToken.None);
        using var harness = new TelegramPollingHarness(ClientFor(server));
        harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage($"/start {code.Code}", username: "laura", userId: Carol)),
            Update(TelegramPollingHarness.TextMessage("/web", username: "laura", userId: Carol)));

        await harness.RunAsync();

        harness.Sent[0].Text.ShouldContain("Linked");
        var link = harness.Sent[1].Text.Split('\n')[^1];
        var page = await server.Client().GetStringAsync(link);
        page.ShouldContain("<strong>Laura</strong>");
    }

    [Fact]
    public async Task AGroupBoundByAnOwnersCode_AdmitsItsLinkedMembers()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var links = server.Service<LinkStore>();
        var franCode = await links.MintTelegramCodeAsync(fran.TenantId, fran.UserId, CancellationToken.None);
        var groupCode = await links.MintGroupCodeAsync(fran.TenantId, fran.UserId, CancellationToken.None);
        using var harness = new TelegramPollingHarness(ClientFor(server));
        await harness.ReceiveAsync();
        harness.Enqueue(
            Update(TelegramPollingHarness.TextMessage($"/start {franCode.Code}", username: "fran", userId: Carol)),
            Update(TelegramPollingHarness.GroupMessage($"/bind {groupCode.Code}", username: "fran", userId: Carol)),
            Update(TelegramPollingHarness.GroupMessage("/dinner ideas", username: "fran", userId: Carol)));

        await harness.RunAsync();

        harness.Sent.Count.ShouldBe(2);
        var principal = (await harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Principal.ShouldNotBeNull();
        principal.Account.ShouldBe(new Account(fran.TenantId, fran.UserId));
    }

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-channel-telegram"] = Credential }
        });

    private static AccountsResolverClient ClientFor(AccountsServer server) =>
        new(new HttpClient { BaseAddress = server.Address }, Credential);

    private static Update Update(Message message) => new() { Id = 1, Message = message };
}