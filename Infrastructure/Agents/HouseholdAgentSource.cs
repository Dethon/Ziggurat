using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Household;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// A household's agents from its configuration (.claude/rules/household-config.md): the agent's view,
// read from the accounts service and kept by the reader for a minute or until a save of the household
// is announced, built into the agents its turns run (HouseholdAgents). No household is the home's.
public sealed class HouseholdAgentSource(
    IHouseholdConfiguration configuration,
    SharedServers sharedServers,
    ILogger<HouseholdAgentSource>? logger = null) : IHouseholdAgentSource
{
    public async Task<HouseholdAgents> ForAsync(string? householdId, CancellationToken ct)
    {
        var household = HouseholdAgents.Of(
            await configuration.ForAgentAsync(householdId ?? HouseholdConfigProtocol.HomeHousehold, ct), sharedServers);
        foreach (var problem in household.Problems)
        {
            logger?.LogWarning("Household {Household}: {Problem}; the agent is left out", household.HouseholdId, problem);
        }

        return household;
    }
}