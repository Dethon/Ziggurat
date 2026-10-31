using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Options;

namespace Infrastructure.Agents;

public class AgentDefinitionProvider(
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    CustomAgentRegistry customAgentRegistry) : IAgentDefinitionProvider
{
    public AgentDefinition? GetById(string agentId, Account? owner = null)
    {
        return registryOptions.CurrentValue.Agents
                   .FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
               ?? (owner is null ? null : customAgentRegistry.Find(owner, agentId));
    }

    public IReadOnlyList<AgentDefinition> GetAll(Account? owner = null)
    {
        var builtIn = registryOptions.CurrentValue.Agents.ToList();

        if (owner is not null)
        {
            builtIn.AddRange(customAgentRegistry.GetByOwner(owner));
        }

        return builtIn;
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