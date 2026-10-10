using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Memory;

public class MemoryToolFeature(IMemoryStore store, IEmbeddingService embeddingService) : IDomainToolFeature
{
    private const string Feature = "memory";

    public string FeatureName => Feature;

    public string? Prompt => MemoryPrompts.FeatureSystemPrompt;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var forgetTool = new MemoryForgetTool(store, embeddingService, config);
        yield return FootprintDeclaration.Declared(
            AIFunctionFactory.Create(
                forgetTool.Run,
                name: $"domain__{Feature}__{MemoryForgetTool.Name}",
                description: MemoryForgetTool.Description),
            MemoryForgetTool.Footprint);
    }
}