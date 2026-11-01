using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

// Where a person's custom agents outlive the agent's process: filed under the household and the
// person who registered them, so an id is only ever found again for that owner.
public interface ICustomAgentStore
{
    Task SaveAsync(Account owner, AgentDefinition definition, CancellationToken ct = default);
    Task RemoveAsync(Account owner, string agentId, CancellationToken ct = default);
    Task<IReadOnlyList<OwnedAgent>> LoadAllAsync(CancellationToken ct = default);
}

public record OwnedAgent(Account Owner, AgentDefinition Definition);