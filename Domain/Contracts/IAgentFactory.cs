using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IAgentFactory
{
    // userId is who the turn's sender names, which memory and the agent's prompt read; account is
    // the account the turn acts for where its channel knew one, which a person's custom agents are
    // filed under. The two differ on a chat-client turn, whose sender is the account's display name.
    DisposableAgent Create(
        AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler, Account? account = null);
    DisposableAgent CreateSubAgent(
        SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn);
}