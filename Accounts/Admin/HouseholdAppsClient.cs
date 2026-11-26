using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Accounts.Settings;
using Accounts.Signing;
using Domain.Apps;
using Domain.Security;

namespace Accounts.Admin;

// The household's published apps, which only the apps server knows: its volume is never read here. It is
// asked on its listing (PublishedApps.Path) with an assertion this service signs for the owner the admin
// gate verified, for the published-apps channel, which the apps server holds the listing to and takes the
// household from — the request names none. The credential is this service's own signing key, which the
// apps server already verifies its viewers' assertions against, so no secret exists for this.
//
// Null is "could not be asked": the server down, refusing, or slower than AppsTimeout.
public sealed class HouseholdAppsClient(
    IHttpClientFactory clients, AccountsSettings settings, Assertions assertions, ILogger<HouseholdAppsClient> logger)
{
    public async Task<IReadOnlyList<PublishedApp>?> PublishedAsync(ForwardedAccount owner, CancellationToken ct)
    {
        using var asking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        asking.CancelAfter(settings.AppsTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(settings.AppsUrl.TrimEnd('/') + "/"), PublishedApps.Path.TrimStart('/')));
            request.Headers.Add(ForwardedAccount.AssertionHeader, assertions.Of(owner, AppChannels.PublishedApps));
            using var response = await clients.CreateClient(nameof(HouseholdAppsClient)).SendAsync(request, asking.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The apps server refused household {Household}'s published apps ({Status})",
                    owner.TenantId, (int)response.StatusCode);
                return null;
            }

            return (await response.Content.ReadFromJsonAsync<PublishedAppList>(asking.Token))?.Apps;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or JsonException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "The apps server could not be asked for household {Household}'s published apps", owner.TenantId);
            return null;
        }
    }
}