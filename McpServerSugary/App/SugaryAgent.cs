using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpServerSugary.Settings;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.App;

// The agent the app's turns go to, as the person's household runs it, read on the assertion her
// request carried: what a photo or a label is checked against before it is sent. None where the
// household's agents cannot be read just now, which is as silent as a catalogue nothing has
// registered yet — the photo goes, and the agent's own answer says what it could not see.
public static class SugaryAgent
{
    public static async Task<AgentCatalogEntry?> OfAsync(
        Person person, HouseholdAgentCatalogue agents, SugarySettings settings, ILogger log, CancellationToken ct)
    {
        try
        {
            return await agents.AgentAsync(
                person.Account.TenantId, HouseholdProof.OfAssertion(person.Assertion), settings.Agent, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not read household {Household}'s agents; the photo is not checked",
                person.Account.TenantId);
            return null;
        }
    }
}