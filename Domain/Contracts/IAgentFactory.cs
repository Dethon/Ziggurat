using Domain.Agents;
using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgentFactory
{
    DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler);
    DisposableAgent CreateSubAgent(
        SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn);
}