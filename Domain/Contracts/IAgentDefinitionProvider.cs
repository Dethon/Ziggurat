using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

// A built-in agent is anybody's; a custom agent is found only for the owner it was registered
// under — the household and the person together — and asked with no owner, the provider answers
// with the built-in agents alone.
public interface IAgentDefinitionProvider
{
    AgentDefinition? GetById(string agentId, Account? owner = null);
    IReadOnlyList<AgentDefinition> GetAll(Account? owner = null);

    Task<AgentDefinition> RegisterCustomAgentAsync(
        Account owner, CustomAgentRegistration registration, CancellationToken ct = default);

    Task<bool> UnregisterCustomAgentAsync(Account owner, string agentId, CancellationToken ct = default);
}