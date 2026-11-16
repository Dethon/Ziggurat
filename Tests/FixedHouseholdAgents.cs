using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;

namespace Tests;

// A stack's agents named in code, the same for every household: what a test drives where no
// household's configuration is what it is about (HouseholdAgentSource is the deployment's).
public sealed class FixedHouseholdAgents(params AgentDefinition[] agents) : IHouseholdAgentSource
{
    public string? DefaultAgent { get; init; }

    public Task<HouseholdAgents> ForAsync(string? householdId, CancellationToken ct) =>
        Task.FromResult(HouseholdAgents.Fixed(agents, DefaultAgent));

    public static AgentDefinitionProvider Provider(params AgentDefinition[] agents) =>
        new(new FixedHouseholdAgents(agents), new CustomAgentRegistry());

    public static AgentDefinitionProvider Provider(CustomAgentRegistry registry, params AgentDefinition[] agents) =>
        new(new FixedHouseholdAgents(agents), registry);
}