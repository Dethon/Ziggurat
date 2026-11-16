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
    //
    // channelId is the channel the conversation's group is built for, whose sections the agent reads
    // whatever its definition selects (PromptManifest.ForChannel): an agent serving a satellite answers
    // under the voice rules. Decided once per group, so a running conversation never changes its rules.
    Task<DisposableAgent> CreateAsync(
        AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler, Account? account = null,
        string? tenantId = null, CancellationToken ct = default, string? channelId = null);
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
// A turn naming an agent its household does not have: refused by id, never answered by another agent.
public sealed class AgentNotFoundException(string? agentId, string? householdId)
    : InvalidOperationException(string.IsNullOrEmpty(agentId)
        ? "No agent id was supplied and no default agent is configured for the channel."
        : $"No agent found for identifier '{agentId}'.")
{
    public string? AgentId { get; } = agentId;

    public string? HouseholdId { get; } = householdId;
}