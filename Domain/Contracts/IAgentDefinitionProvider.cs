using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

// A household's agents are its own (IHouseholdAgentSource); a custom agent is found only for the
// owner it was registered under — the household and the person together — and asked with no owner,
// the provider answers with the household's agents alone. A household is named by id; none is the
// home household's, which a turn acting for nobody runs.
public interface IAgentDefinitionProvider
{
    Task<HouseholdAgents> ForHouseholdAsync(string? householdId, CancellationToken ct = default);

    Task<AgentDefinition?> GetByIdAsync(
        string agentId, string? householdId, Account? owner = null, CancellationToken ct = default);

    Task<IReadOnlyList<AgentDefinition>> GetAllAsync(
        string? householdId, Account? owner = null, CancellationToken ct = default);

    Task<AgentDefinition> RegisterCustomAgentAsync(
        Account owner, CustomAgentRegistration registration, CancellationToken ct = default);

    Task<bool> UnregisterCustomAgentAsync(Account owner, string agentId, CancellationToken ct = default);
}