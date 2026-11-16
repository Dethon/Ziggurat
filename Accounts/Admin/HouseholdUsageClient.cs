using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Accounts.Settings;
using Accounts.Signing;
using Domain.DTOs.Metrics;
using Domain.Security;

namespace Accounts.Admin;

// What a household used, which only the Observability host knows: its metrics are never read here.
// It is asked on its usage route (HouseholdUsage.Path) with an assertion this service signs for the
// owner the admin gate verified, for the usage channel, which the host holds the route to and takes
// the household from — the request names none. The credential is this service's own signing key,
// which the host already verifies against, so no secret exists for this.
//
// Null is "could not be asked": the host down, refusing, or slower than UsageTimeout.
public sealed class HouseholdUsageClient(
    IHttpClientFactory clients, AccountsSettings settings, Assertions assertions, ILogger<HouseholdUsageClient> logger)
{
    public async Task<HouseholdUsage?> ReadAsync(ForwardedAccount owner, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var asking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        asking.CancelAfter(settings.UsageTimeout);
        try
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"{HouseholdUsage.Path.TrimStart('/')}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(settings.ObservabilityUrl.TrimEnd('/') + "/"), path));
            request.Headers.Add(ForwardedAccount.AssertionHeader, assertions.Of(owner, AppChannels.Usage));
            using var response = await clients.CreateClient(nameof(HouseholdUsageClient)).SendAsync(request, asking.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The Observability host refused household {Household}'s usage ({Status})",
                    owner.TenantId, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<HouseholdUsage>(asking.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "The Observability host could not be asked for household {Household}'s usage", owner.TenantId);
            return null;
        }
    }
}