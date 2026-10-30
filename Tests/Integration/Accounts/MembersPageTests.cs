using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Accounts.Links;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household as its owners manage it: who is in it and as what, the links that let somebody in,
// and the permission to see every conversation. Enrolling through an invite or a recovery link is in
// PasskeyCeremoniesTests, which can make a passkey.
public partial class MembersPageTests
{
    private const string Members = "/accounts/members";

    [Fact]
    public async Task AMember_IsRefusedTheMembersPageAndItsForms()
    {
        await using var server = await AccountsServer.StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.SignedIn(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await laura.GetAsync(Members)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.PostAsync("/accounts/account", $"{Members}/invite", ("displayName", "Pablo"), ("role", "owner")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnOwner_SeesEveryoneWithTheirRoleOperatorFlagAndPermission()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        await server.SeedMemberAsync("Laura");

        var page = await server.SignedIn(await server.MintSessionAsync(fran.UserId)).PageAsync(Members);

        Row(page, "Fran").ShouldContain("data-role>owner<");
        Row(page, "Fran").ShouldContain(">operator<");
        Row(page, "Laura").ShouldContain("data-role>member<");
        Row(page, "Laura").ShouldNotContain(">operator<");
        Row(page, "Laura").ShouldContain("Grant sees all conversations");
    }

    [Fact]
    public async Task AnInvite_OpensAnEnrollmentPageForTheNamedPerson()
    {
        await using var server = await AccountsServer.StartAsync();
        var owner = await OwnerAsync(server);

        var invite = await InviteAsync(owner, "Laura", "member");

        var page = await server.Client().GetAsync(invite);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("Laura");
    }

    [Fact]
    public async Task AnInvite_IsRefusedOnceItsWeekIsUp()
    {
        await using var server = await AccountsServer.StartAsync();
        var invite = await InviteAsync(await OwnerAsync(server), "Laura", "member");

        server.Clock.Advance(TimeSpan.FromDays(7));

        (await server.Client().GetAsync(invite)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await server.Client().PostAsJsonAsync("/accounts/api/enroll/options", new { token = invite.Segments[^1] }))
            .StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    // Redeemed the way Telegram's start parameter will redeem it, without a passkey: the person is
    // created as the invite named them, and the link opens nothing after.
    [Fact]
    public async Task AnInviteRedeemedOnce_CreatesThePersonAndThenRefuses()
    {
        await using var server = await AccountsServer.StartAsync();
        var owner = await OwnerAsync(server);
        var invite = await InviteAsync(owner, "Laura", "owner");
        var links = server.Service<LinkStore>();
        var open = (await links.FindOpenAsync(invite.Segments[^1], [LinkKind.Invite], CancellationToken.None)).ShouldNotBeNull();

        await using (var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var laura = (await links.RedeemInviteAsync(db, open.LinkId, Tokens.NewId(), CancellationToken.None)).ShouldNotBeNull();
            await transaction.CommitAsync();
            laura.DisplayName.ShouldBe("Laura");
            laura.Role.ShouldBe(Role.Owner);
        }

        (await server.Client().GetAsync(invite)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        Row(await owner.PageAsync(Members), "Laura").ShouldContain("data-role>owner<");
    }

    [Fact]
    public async Task ARecoveryLink_OpensAnEnrollmentPageForThatMember()
    {
        await using var server = await AccountsServer.StartAsync();
        var owner = await OwnerAsync(server);
        var laura = await server.SeedMemberAsync("Laura");

        var response = await owner.PostAsync(Members, $"{Members}/{laura}/recovery");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = LinkIn(await response.Content.ReadAsStringAsync());
        (await (await server.Client().GetAsync(link)).Content.ReadAsStringAsync()).ShouldContain("Laura");
    }

    // The operator's own way back in is the container command: an owner who could mint them a
    // recovery link could take the deployment's dashboard.
    [Fact]
    public async Task AnotherOwner_CannotMintTheOperatorARecoveryLink()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var owner = server.SignedIn(await server.MintSessionAsync(fran.UserId));
        var laura = await server.SeedMemberAsync("Laura");
        (await owner.PostAsync(Members, $"{Members}/{laura}/role", ("role", "owner"))).StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        var response = await server.SignedIn(await server.MintSessionAsync(laura))
            .PostAsync(Members, $"{Members}/{fran.UserId}/recovery");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChangingARole_IsWhatTheSessionCheckThenSays()
    {
        await using var server = await AccountsServer.StartAsync();
        var owner = await OwnerAsync(server);
        var laura = await server.SeedMemberAsync("Laura");
        var lauras = await server.MintSessionAsync(laura);

        (await owner.PostAsync(Members, $"{Members}/{laura}/role", ("role", "owner"))).StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        (await server.VerifyAsync(lauras)).Headers.GetValues("X-Ziggurat-Roles").ShouldBe(["owner"]);
        Row(await owner.PageAsync(Members), "Laura").ShouldContain("data-role>owner<");
    }

    [Fact]
    public async Task TheLastOwner_CannotBeDemoted()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var owner = server.SignedIn(await server.MintSessionAsync(fran.UserId));

        var response = await owner.PostAsync(Members, $"{Members}/{fran.UserId}/role", ("role", "member"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await server.VerifyAsync(await server.MintSessionAsync(fran.UserId))).Headers.GetValues("X-Ziggurat-Roles")
            .ShouldBe(["owner,operator"]);
    }

    [Fact]
    public async Task WithASecondOwner_AnOwnerCanStepDown()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var owner = server.SignedIn(await server.MintSessionAsync(fran.UserId));
        var laura = await server.SeedMemberAsync("Laura");
        await owner.PostAsync(Members, $"{Members}/{laura}/role", ("role", "owner"));

        var response = await owner.PostAsync(Members, $"{Members}/{fran.UserId}/role", ("role", "member"));

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        (await owner.GetAsync(Members)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SeesAllConversations_IsGrantedAndWithdrawn()
    {
        await using var server = await AccountsServer.StartAsync();
        var owner = await OwnerAsync(server);
        var laura = await server.SeedMemberAsync("Laura");

        await owner.PostAsync(Members, $"{Members}/{laura}/sees-all", ("grant", "true"));
        Row(await owner.PageAsync(Members), "Laura").ShouldContain("Withdraw sees all conversations");

        await owner.PostAsync(Members, $"{Members}/{laura}/sees-all", ("grant", "false"));
        Row(await owner.PageAsync(Members), "Laura").ShouldContain("Grant sees all conversations");
    }

    private static async Task<AccountsServer.PageClient> OwnerAsync(AccountsServer server)
    {
        var fran = await server.MintOperatorAsync("Fran");
        return server.SignedIn(await server.MintSessionAsync(fran.UserId));
    }

    private static async Task<Uri> InviteAsync(AccountsServer.PageClient owner, string displayName, string role)
    {
        var response = await owner.PostAsync(Members, $"{Members}/invite", ("displayName", displayName), ("role", role));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return LinkIn(await response.Content.ReadAsStringAsync());
    }

    private static Uri LinkIn(string page)
    {
        var link = LinkPattern().Match(page);
        link.Success.ShouldBeTrue(page);
        return new Uri(link.Groups["url"].Value);
    }

    // A person's row on the members page.
    private static string Row(string page, string displayName) =>
        RowPattern().Matches(page).Select(m => m.Value).Single(row => row.Contains($">{displayName}<", StringComparison.Ordinal));

    [GeneratedRegex("""<a data-link href="(?<url>[^"]+)">""")]
    private static partial Regex LinkPattern();

    [GeneratedRegex("<tr data-member.*?</tr>", RegexOptions.Singleline)]
    private static partial Regex RowPattern();
}