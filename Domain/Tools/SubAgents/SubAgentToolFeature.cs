using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentToolFeature(
    SubAgentRegistryOptions registryOptions) : IDomainToolFeature
{
    private const string Feature = "subagents";

    public string FeatureName => Feature;

    public string? Prompt => SubAgentPrompt.SystemPrompt;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var runTool = new SubAgentRunTool(registryOptions, config);
        // A worker can read anything the calls written before it changed, so it waits for
        // those, and a change written after it waits for the worker. Workers handed out in one
        // message are parts the model has already called independent: they run side by side,
        // and what each does inside its own task is ordered by its own client.
        yield return FootprintDeclaration.Declared(
            AIFunctionFactory.Create(
                runTool.RunAsync,
                name: $"domain__{Feature}__{SubAgentRunTool.Name}",
                description: runTool.Description),
            CallFootprint.Reading(SharedResource.Everything));
    }
}