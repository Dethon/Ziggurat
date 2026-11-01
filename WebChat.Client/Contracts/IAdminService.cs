using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

// The admin API on the accounts service, as the household's owners reach it from the admin area.
public interface IAdminService
{
    Task<AdminAnswer<HouseholdSettings>> GetSettingsAsync();

    Task<AdminAnswer<HouseholdSettings>> SaveSettingsAsync(HouseholdSettings settings);

    // The household's keys as their states and last characters; each key is saved on its own and
    // never read back.
    Task<AdminAnswer<HouseholdKeys>> GetKeysAsync();

    Task<AdminAnswer<HouseholdKeys>> SaveOpenRouterKeyAsync(OpenRouterKey key);

    Task<AdminAnswer<HouseholdKeys>> SaveIdealistaKeysAsync(IdealistaKeys keys);

    Task<AdminAnswer<HouseholdKeys>> SaveTelegramBotAsync(string agentId, TelegramBotKey bot);
    Task<AdminAnswer<AdminAgents>> GetAgentsAsync();

    Task<AdminAnswer<AdminAgents>> SaveAgentAsync(string agentId, AgentEdit edit);

    Task<AdminAnswer<AdminAgents>> AddAgentAsync(AgentAddition addition);

    Task<AdminAnswer<AdminAgents>> RemoveAgentAsync(string agentId);

    Task<AdminAnswer<IReadOnlyList<AdminAgentVersion>>> GetAgentVersionsAsync(string agentId);

    Task<AdminAnswer<AdminAgents>> RestoreAgentVersionAsync(string agentId, string versionId);
}