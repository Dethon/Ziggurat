using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace Infrastructure.Agents;

public class AgentDefinitionProvider(
    IHouseholdAgentSource households,
    CustomAgentRegistry customAgentRegistry) : IAgentDefinitionProvider
{
    public Task<HouseholdAgents> ForHouseholdAsync(string? householdId, CancellationToken ct = default) =>
        households.ForAsync(householdId, ct);

    // The household's own agent wins an id a person's agent also carries: a custom agent's id is
    // minted (custom-<guid>), so a clash is somebody's registration naming the household's agent.
    public async Task<AgentDefinition?> GetByIdAsync(
        string agentId, string? householdId, Account? owner = null, CancellationToken ct = default)
    {
        var household = await households.ForAsync(householdId, ct);
        return household.Find(agentId)
               ?? (owner is null ? null : customAgentRegistry.Find(owner, agentId) is { } custom ? household.Offered(custom) : null);
    }

    public async Task<IReadOnlyList<AgentDefinition>> GetAllAsync(
        string? householdId, Account? owner = null, CancellationToken ct = default)
    {
        var household = await households.ForAsync(householdId, ct);
        return
        [
            .. household.Agents,
            .. owner is null ? [] : customAgentRegistry.GetByOwner(owner).Select(household.Offered)
        ];
    }

    public async Task<AgentDefinition> RegisterCustomAgentAsync(
        Account owner, CustomAgentRegistration registration, CancellationToken ct = default)
    {
        var definition = new AgentDefinition
        {
            Id = $"custom-{Guid.NewGuid()}",
            Name = registration.Name,
            Description = registration.Description,
            Model = registration.Model,
            McpServerEndpoints = registration.McpServerEndpoints,
            WhitelistPatterns = registration.WhitelistPatterns,
            CustomInstructions = registration.CustomInstructions,
            EnabledFeatures = registration.EnabledFeatures,
            ProviderRouting = registration.ProviderRouting
        };

        await customAgentRegistry.AddAsync(owner, definition, ct);

        return definition;
    }

    public Task<bool> UnregisterCustomAgentAsync(Account owner, string agentId, CancellationToken ct = default) =>
        customAgentRegistry.RemoveAsync(owner, agentId, ct);
}