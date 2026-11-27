using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Domain.Apps;
using Domain.Contracts;
using Domain.Security;
using Infrastructure.Clients.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Apps;

// A person's apps for WebChat's space switcher (PersonsApps), as the chat hub asks them: the accounts
// service's word for the connection's session on AppChannels.PersonsApps (the session refresh, asked with
// the hub's own credential, which alone may name that channel), carried to the apps server's listing on
// its MCP listener. The credential is the accounts service's signature, which the apps server already
// verifies its viewers' assertions against, so no secret exists for this.
//
// Null is "not asked": no apps server configured (appsUrl empty), the session gone, the accounts service
// or the apps server unreachable, refusing or slower than the timeout. The switcher then lists the chat
// spaces alone; nothing of the chat waits on the apps server.
public sealed class PersonsAppsClient(
    AccountsResolverClient accounts, HttpClient http, string appsUrl, TimeSpan timeout, ILogger<PersonsAppsClient> logger)
    : IPersonsApps
{
    public const string ClientName = "persons-apps";

    public async Task<IReadOnlyList<PersonsApp>?> ListAsync(
        string userId, string sessionId, IReadOnlyCollection<string> shared, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(appsUrl))
        {
            return null;
        }

        using var asking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        asking.CancelAfter(timeout);
        try
        {
            if (await accounts.SessionOnAsync(userId, sessionId, AppChannels.PersonsApps, asking.Token) is not { } person)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(appsUrl.TrimEnd('/') + "/"), PersonsApps.PathFor(shared).TrimStart('/')));
            request.Headers.Add(ForwardedAccount.AssertionHeader, person.Assertion);
            using var response = await http.SendAsync(request, asking.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The apps server refused account {UserId}'s apps ({Status})", userId, (int)response.StatusCode);
                return null;
            }

            return (await response.Content.ReadFromJsonAsync<PersonsAppList>(asking.Token))?.Apps;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or JsonException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Account {UserId}'s apps could not be asked for", userId);
            return null;
        }
    }
}

public static class PersonsAppsRegistration
{
    // The accounts client must be registered too (AddAccountResolver): the session refresh is asked
    // through it with the host's own credential.
    public static IServiceCollection AddPersonsApps(this IServiceCollection services, string appsUrl, TimeSpan timeout)
    {
        services.AddHttpClient(PersonsAppsClient.ClientName);
        return services.AddSingleton<IPersonsApps>(sp => new PersonsAppsClient(
            sp.GetRequiredService<AccountsResolverClient>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(PersonsAppsClient.ClientName),
            appsUrl, timeout, sp.GetRequiredService<ILogger<PersonsAppsClient>>()));
    }
}