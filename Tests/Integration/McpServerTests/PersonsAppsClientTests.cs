using Accounts.Sessions;
using Accounts.Settings;
using Domain.Apps;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// How the chat hub learns a person's apps for WebChat's space switcher, against a real accounts service
// and a real apps server: the accounts service's word for the connection's session on persons-apps, asked
// with the hub's own credential, carried to the apps server's listing. Anything that stops it — the session
// gone, an apps server unreachable or not configured — is no apps, never a failure of the hub's call.
public class PersonsAppsClientTests
{
    private const string HubCredential = "hub-credential";

    [Fact]
    public async Task ALiveSession_IsToldItsPersonsApps()
    {
        await using var accounts = await StartAccountsAsync();
        await using var apps = await StartAppsAsync(accounts);
        var (fran, session) = await SignedInAsync(accounts);
        var id = await PublishedAsync(apps, fran, "glucose");

        var listed = await ClientFor(accounts, apps.McpAddress).ListAsync(fran.UserId, session, [], CancellationToken.None);

        listed.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(
            new PersonsApp(id, "Glucose today", "#e9601f", $"https://glucose-{id}.{AppsServer.AppsDomain}/", Own: true));
    }

    [Fact]
    public async Task ARevokedSession_IsToldNoApps()
    {
        await using var accounts = await StartAccountsAsync();
        await using var apps = await StartAppsAsync(accounts);
        var (fran, session) = await SignedInAsync(accounts);
        await PublishedAsync(apps, fran, "glucose");
        await accounts.Service<SessionStore>().RevokeAsync(fran.UserId, session, CancellationToken.None);

        (await ClientFor(accounts, apps.McpAddress).ListAsync(fran.UserId, session, [], CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AnAppsServerThatDoesNotAnswer_IsNoApps()
    {
        await using var accounts = await StartAccountsAsync();
        var (fran, session) = await SignedInAsync(accounts);

        (await ClientFor(accounts, DeadEnd.Url()).ListAsync(fran.UserId, session, [], CancellationToken.None)).ShouldBeNull();
    }

    // A deployment with no apps server lists nobody's apps, and asks the accounts service nothing for it.
    [Fact]
    public async Task NoAppsServer_IsNoApps_AskedOfNobody()
    {
        var client = new PersonsAppsClient(
            new AccountsResolverClient(new HttpClient { BaseAddress = new Uri(DeadEnd.Url()) }, HubCredential),
            new HttpClient(), "", TimeSpan.FromSeconds(3), NullLogger<PersonsAppsClient>.Instance);

        (await client.ListAsync("user", "session", [], CancellationToken.None)).ShouldBeNull();
    }

    private static Task<AccountsServer> StartAccountsAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Signing = new SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
            Callers = new Dictionary<string, string> { ["mcp-channel-signalr"] = HubCredential }
        });

    // The apps server believes the accounts service's assertions on that service's clock.
    private static Task<AppsServer> StartAppsAsync(AccountsServer accounts) =>
        AppsServer.StartAsync(services: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(accounts.Clock)));

    private static async Task<(Account Person, string Session)> SignedInAsync(AccountsServer accounts)
    {
        var fran = await accounts.MintOperatorAsync("Fran");
        var session = await accounts.Service<SessionStore>().IssueAsync(fran.UserId, null, "phone", CancellationToken.None);
        return (new Account(fran.TenantId, fran.UserId), session.ShouldNotBeNull().Id);
    }

    private static async Task<string> PublishedAsync(AppsServer apps, Account author, string slug)
    {
        var id = AppsServer.IdIn(await apps.AppAsync(author, slug));
        await apps.CreateAsync(author, $"/{slug}/index.html", "<h1>Hi</h1>");
        await apps.ExecAsync(author, $"/{slug}", "./publish");
        return id;
    }

    private static PersonsAppsClient ClientFor(AccountsServer accounts, string appsUrl) => new(
        new AccountsResolverClient(new HttpClient { BaseAddress = accounts.Address }, HubCredential),
        new HttpClient(), appsUrl, TimeSpan.FromSeconds(3), NullLogger<PersonsAppsClient>.Instance);
}