using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolRegistry
{
    IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config);
    IEnumerable<PromptSection> GetPromptsForFeatures(IEnumerable<string> enabledFeatures);
}