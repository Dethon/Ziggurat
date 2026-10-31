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
    //
    // tenantId is the household the turn is for — a person's, or the guest's — whose agents it runs
    // and whose own servers the agent reaches. Null for a turn that acts for nobody, which runs the
    // home household's agents and reaches no household's servers.
    Task<DisposableAgent> CreateAsync(
        AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler, Account? account = null,
        string? tenantId = null, CancellationToken ct = default);
    DisposableAgent CreateSubAgent(
        SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn);
}

// A household of its own that set no OpenRouter key pays for nothing, so its turns are refused before
// any model is asked: the deployment's key is the home household's, never another's to fall back on.
public sealed class HouseholdWithoutKeyException(string householdId)
    : Exception($"Household {householdId} has set no OpenRouter key, so its turns are not run.")
{
    public string HouseholdId { get; } = householdId;
}