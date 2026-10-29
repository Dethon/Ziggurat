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

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-channel-signalr"] = Credential }
        });

    private static AccountsResolverClient ClientFor(AccountsServer server, string credential) =>
        new(new HttpClient { BaseAddress = server.Address }, credential);
}