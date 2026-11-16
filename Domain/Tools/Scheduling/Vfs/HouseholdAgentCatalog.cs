using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;

namespace Domain.Tools.Scheduling.Vfs;

// A household's own agents as the schedules mount shows them: one directory each, its agent_info.json
// the id, name and description the household's configuration gives it, never its instructions.
internal sealed class HouseholdAgentCatalog(HouseholdAgentNames names) : IAgentCatalog
{
    private readonly IReadOnlyList<AgentCatalogEntry> _agents =
    [
        .. names.Agents.Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description, a.Model, a.ReasoningEffort))
    ];

    public IReadOnlyList<AgentCatalogEntry> GetAll() => _agents;

    public AgentCatalogEntry? Get(string agentId) => _agents.FirstOrDefault(a => a.Id == agentId);

    public bool Exists(string agentId) => Get(agentId) is not null;
}