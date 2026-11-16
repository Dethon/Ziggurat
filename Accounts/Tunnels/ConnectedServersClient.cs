using System.Net.Http.Json;
using System.Text.Json;
using Accounts.Internal;
using Accounts.Settings;
using Domain.DTOs.Tunnel;
using Domain.Security;

namespace Accounts.Tunnels;

// Which of a household's own servers are connected now. The relay that knows lives in the agent, in
// its serving process alone, so this asks the agent's own listing (ConnectedServers.Path, under its
// /internal, which the proxy never routes) as the operator's list-household-servers does. The
// credential is the one the agent presents to this service, CALLERS__agent, which compose feeds the
// agent API's own secret: so no secret exists for this alone.
//
// Null is "could not be asked" — no agent credential configured, the agent down, refusing, or slower
// than the listing timeout — which a page shows as unknown rather than as nothing connected.
public sealed class ConnectedServersClient(
    IHttpClientFactory clients, AccountsSettings settings, ILogger<ConnectedServersClient> logger)
{
    public async Task<IReadOnlyList<ConnectedServer>?> ListAsync(string householdId, CancellationToken ct)
    {
        if (settings.Callers.GetValueOrDefault(InternalCallers.Agent) is not { Length: > 0 } credential)
        {
            return null;
        }

        using var asking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        asking.CancelAfter(settings.AgentListingTimeout);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, new Uri(new Uri(settings.AgentUrl.TrimEnd('/') + "/"), ConnectedServers.For(householdId).TrimStart('/')));
            request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
            using var response = await clients.CreateClient(nameof(ConnectedServersClient)).SendAsync(request, asking.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The agent refused the listing of household {Household}'s servers ({Status})",
                    householdId, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<IReadOnlyList<ConnectedServer>>(
                TunnelProtocol.SerializerOptions, asking.Token) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "The agent could not be asked which of household {Household}'s servers are connected",
                householdId);
            return null;
        }
    }
}