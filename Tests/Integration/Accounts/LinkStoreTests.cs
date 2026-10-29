using System.Net;
using System.Net.Http.Json;
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

    // Minting a fresh link for the operator leaves the one already handed out working: recovering
    // is adding a way in, not closing one.
    [Fact]
    public async Task MintingAnotherLink_LeavesTheFirstOpen()
    {
        await using var server = await AccountsServer.StartAsync();
        var first = await server.MintOperatorAsync("Fran");

        await server.MintOperatorAsync("Fran");

        (await server.Client().GetAsync(first.Url)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> BeginAsync(AccountsServer server, string token) =>
        server.Client().PostAsJsonAsync("/accounts/api/enroll/options", new { token });
}