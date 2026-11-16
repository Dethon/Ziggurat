using Domain.Agents;

namespace Domain.Contracts;

// Where a household's agents come from: its configuration, read from the accounts service and kept
// briefly (HouseholdAgentSource), or a stack's own list. A turn that acts for no household — a fire
// whose delegation was not believed — runs the home household's agents, which are the deployment's
// own, and reaches no household's servers by it (its spec still names no household).
//
// Throws what reading the configuration threw: a household the agent cannot read is not one with no
// agents.
public interface IHouseholdAgentSource
{
    Task<HouseholdAgents> ForAsync(string? householdId, CancellationToken ct);
}