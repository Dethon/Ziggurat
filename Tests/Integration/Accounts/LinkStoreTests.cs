using System.Net;
using System.Net.Http.Json;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A link's week, at the two places a browser meets it: the page the link opens, and the ceremony
// that page begins. A used link is refused the same way (PasskeyCeremoniesTests, which can use one).
public class LinkStoreTests
{
    [Fact]
    public async Task ALinkOnTheLastMinuteOfItsWeek_StillOpens()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        server.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));

        (await server.Client().GetAsync(fran.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BeginAsync(server, fran.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnExpiredLink_RefusesThePageAndTheCeremony()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");

        server.Clock.Advance(TimeSpan.FromDays(7));

        var page = await server.Client().GetAsync(fran.Url);
        page.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await page.Content.ReadAsStringAsync()).ShouldContain("can no longer be used");
        (await BeginAsync(server, fran.Token)).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task ATokenNobodyMinted_IsRefused()
    {
        await using var server = await AccountsServer.StartAsync();
        await server.MintOperatorAsync("Fran");

        (await server.Client().GetAsync("/accounts/enroll/made-up")).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await BeginAsync(server, "made-up")).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    // A recovery link sent to the wrong person could not be taken back: the owner minted a fresh one
    // and the first stayed open for its week, so whoever held it could still add a passkey to the
    // account. A person's newest recovery link closes their earlier ones, whoever minted which.
    [Fact]
    public async Task MintingAnotherRecoveryLink_ClosesTheEarlierOnes()
    {
        await using var server = await AccountsServer.StartAsync();
        var first = await server.MintOperatorAsync("Fran");
        var sara = await server.SeedMemberAsync("Sara");
        var links = server.Service<global::Accounts.Links.LinkStore>();
        var sarasFirst = Minted(await links.MintRecoveryAsync(sara, CancellationToken.None));
        var invite = await links.MintInviteAsync(first.TenantId, first.UserId, "Iker", Role.Member, CancellationToken.None);

        var second = await server.MintOperatorAsync("Fran");
        var sarasSecond = Minted(await links.MintRecoveryAsync(first.TenantId, first.UserId, sara, CancellationToken.None));

        (await server.Client().GetAsync(first.Url)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await BeginAsync(server, first.Token)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await server.Client().GetAsync(second.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await server.Client().GetAsync(sarasFirst.Url)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await server.Client().GetAsync(sarasSecond.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await server.Client().GetAsync(invite.Url)).StatusCode.ShouldBe(HttpStatusCode.OK, "an invite names nobody yet");
    }

    // An owner, signed in, who opened another person's recovery link was asked nothing and became that
    // person on their own device: the page says whose this device is now and whose it will be.
    [Fact]
    public async Task ALinkOpenedOnADeviceSignedInAsSomebodyElse_SaysSo()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var sara = await server.SeedMemberAsync("Sara");
        var link = Minted(await server.Service<global::Accounts.Links.LinkStore>().MintRecoveryAsync(sara, CancellationToken.None));

        var asFran = await server.SignedIn(await server.MintSessionAsync(fran.UserId)).PageAsync(link.Url.PathAndQuery);
        var asSara = await server.SignedIn(await server.MintSessionAsync(sara)).PageAsync(link.Url.PathAndQuery);
        var signedOut = await (await server.Client().GetAsync(link.Url)).Content.ReadAsStringAsync();

        asFran.ShouldContain("This device is signed in as <strong>Fran</strong>");
        asFran.ShouldContain("signs it in as Sara instead");
        asSara.ShouldNotContain("This device is signed in as");
        signedOut.ShouldNotContain("This device is signed in as");
    }

    // Pablo, an owner, minted an owner invite for Zoe; Lola demoted him, and the invite still made Zoe an
    // owner — a demoted or disabled owner could keep a seat through what they had handed out. What an
    // owner hands out (an invite, a group code, another person's recovery link) opens only while they
    // still manage the household, and again once they do (user's decision, 2026-11-05); the operator's
    // and a person's own are untouched.
    [Fact]
    public async Task WhatAnOwnerHandedOut_OpensOnlyWhileTheyStillManageTheHousehold()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (casa, lola) = await server.SeedHouseholdAsync("Casa", "Lola");
        var pablo = await server.SeedPersonAsync(casa, "Pablo", Role.Owner);
        var marta = await server.SeedPersonAsync(casa, "Marta");
        var links = server.Service<global::Accounts.Links.LinkStore>();
        var zoes = await links.MintInviteAsync(casa, pablo, "Zoe", Role.Owner, CancellationToken.None);
        var martas = Minted(await links.MintRecoveryAsync(casa, pablo, marta, CancellationToken.None));
        var group = await links.MintGroupCodeAsync(casa, pablo, CancellationToken.None);
        var lolas = await links.MintInviteAsync(casa, lola, "Iker", Role.Member, CancellationToken.None);
        var operators = Minted(await links.MintRecoveryAsync(casa, fran.UserId, lola, CancellationToken.None));

        async Task<HttpStatusCode[]> PablosAsync() =>
        [
            (await server.Client().GetAsync(zoes.Url)).StatusCode,
            (await BeginAsync(server, zoes.Token)).StatusCode,
            (await server.Client().GetAsync(martas.Url)).StatusCode,
            await links.FindOpenAsync(group.Code, [LinkKind.TelegramGroup], CancellationToken.None) is null ? HttpStatusCode.Gone : HttpStatusCode.OK
        ];

        await SetAsync(server, pablo, u => u.Role = Role.Member);
        (await PablosAsync()).ShouldAllBe(s => s == HttpStatusCode.Gone, "demoted");
        (await server.Client().GetAsync(lolas.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await server.Client().GetAsync(operators.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetAsync(server, pablo, u => u.Role = Role.Owner);
        (await PablosAsync()).ShouldAllBe(s => s == HttpStatusCode.OK, "an owner again");

        await SetAsync(server, pablo, u => u.DisabledAt = server.Clock.GetUtcNow());
        (await PablosAsync()).ShouldAllBe(s => s == HttpStatusCode.Gone, "disabled");
    }

    private static async Task SetAsync(AccountsServer server, string userId, Action<User> change)
    {
        await using var db = await server.Service<Microsoft.EntityFrameworkCore.IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        change(await db.Users.SingleAsync(u => u.Id == userId));
        await db.SaveChangesAsync();
    }

    private static global::Accounts.Links.MintedLink Minted(global::Accounts.Links.RecoveryMint mint) =>
        mint.ShouldBeOfType<global::Accounts.Links.RecoveryMint.Minted>().Link;

    private static Task<HttpResponseMessage> BeginAsync(AccountsServer server, string token) =>
        server.Client().PostAsJsonAsync("/accounts/api/enroll/options", new { token });
}