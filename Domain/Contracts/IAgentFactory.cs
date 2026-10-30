using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IAgentFactory
{
    // userId is who the turn's sender names, which the agent's prompt reads; account is the account
    // the turn acts for where its channel knew one, which a person's custom agents are filed under.
    // The two differ on a chat-client turn, whose sender is the account's display name. Memory reads
    // neither from here: it takes each turn's own account, since a later turn may be someone else's.
    DisposableAgent Create(
        AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler, Account? account = null);
    DisposableAgent CreateSubAgent(
        SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn);
}