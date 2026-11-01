using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

// The admin API on the accounts service, as the household's owners reach it from the admin area.
public interface IAdminService
{
    Task<AdminAnswer<HouseholdSettings>> GetSettingsAsync();

    Task<AdminAnswer<HouseholdSettings>> SaveSettingsAsync(HouseholdSettings settings);

    Task<AdminAnswer<AdminAgents>> GetAgentsAsync();

    Task<AdminAnswer<AdminAgents>> SaveAgentAsync(string agentId, AgentEdit edit);

    Task<AdminAnswer<AdminAgents>> AddAgentAsync(AgentAddition addition);

    Task<AdminAnswer<AdminAgents>> RemoveAgentAsync(string agentId);

    Task<AdminAnswer<IReadOnlyList<AdminAgentVersion>>> GetAgentVersionsAsync(string agentId);

    Task<AdminAnswer<AdminAgents>> RestoreAgentVersionAsync(string agentId, string versionId);

    Task<AdminAnswer<AdminMembers>> GetMembersAsync();

    Task<AdminAnswer<MemberLink>> InviteMemberAsync(MemberInvite invite);

    Task<AdminAnswer<AdminMembers>> SaveMemberAsync(string userId, MemberEdit edit);

    Task<AdminAnswer<AdminMembers>> DisableMemberAsync(string userId);

    Task<AdminAnswer<AdminMembers>> EnableMemberAsync(string userId);

    Task<AdminAnswer<MemberLink>> MintMemberRecoveryAsync(string userId);
}