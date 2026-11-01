using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;

namespace Domain.Agents;

// A household's agents as a channel shows and checks them: the household's own, read from its
// configuration on the proof of whoever is asking (the agent-names view), each on the model it runs
// there, with what the deployment decides about models — the patchable models, the efforts, what a
// model accepts — taken from the catalogue the agent registered (AgentCatalogBuilder.ForHousehold).
//
// The registered catalogue is the home's agents and nobody else's, so it is never read as a
// household's list: a household's own agent may go by an id the home lacks, or run another model
// under an id the home shares. Every channel that asks "which agent is this, and what does it read"
// for a person asks here.
public sealed class HouseholdAgentCatalogue(IHouseholdConfiguration households, IAgentCatalog registered)
{
    public async Task<IReadOnlyList<AgentCatalogEntry>> AgentsAsync(
        string householdId, HouseholdProof proof, CancellationToken ct) =>
        AgentCatalogBuilder.ForHousehold(await households.AgentNamesAsync(householdId, proof, ct), registered.GetAll());

    // The agent a message goes to: the one named, or the household's default where none is. Null
    // where the household has no such agent.
    public async Task<AgentCatalogEntry?> AgentAsync(
        string householdId, HouseholdProof proof, string? agentId, CancellationToken ct)
    {
        var names = await households.AgentNamesAsync(householdId, proof, ct);
        var id = agentId ?? names.DefaultAgent;
        return id is null
            ? null
            : AgentCatalogBuilder.ForHousehold(names, registered.GetAll()).FirstOrDefault(entry => entry.Id == id);
    }
}