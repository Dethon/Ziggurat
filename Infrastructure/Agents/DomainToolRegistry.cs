using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public class DomainToolRegistry(IEnumerable<IDomainToolFeature> features) : IDomainToolRegistry
{
    private readonly Dictionary<string, IDomainToolFeature> _features =
        features.ToDictionary(f => f.FeatureName, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config)
    {
        return GroupByFeature(enabledFeatures)
            .Where(g => _features.ContainsKey(g.FeatureName))
            .SelectMany(g =>
            {
                var featureConfig = config with { EnabledTools = g.EnabledTools };
                return _features[g.FeatureName].GetTools(featureConfig);
            });
    }

    // A feature's prompt is declared in the manifest under the feature's own name, so enabling a
    // feature is what puts its section in the prompt and there is no second list to keep in step.
    public IEnumerable<PromptSection> GetPromptsForFeatures(IEnumerable<string> enabledFeatures)
    {
        return GroupByFeature(enabledFeatures)
            .Where(g => _features.ContainsKey(g.FeatureName))
            .Select(g => (g.FeatureName, _features[g.FeatureName].Prompt))
            .Where(f => !string.IsNullOrWhiteSpace(f.Prompt))
            .Select(f => PromptManifest.Bind(f.FeatureName, f.Prompt!));
    }

    private static IEnumerable<FeatureGroup> GroupByFeature(IEnumerable<string> enabledFeatures)
    {
        return enabledFeatures
            .Select(f => f.Split('.', 2))
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var hasBare = group.Any(parts => parts.Length == 1);
                var enabledTools = hasBare
                    ? null
                    : group
                        .Where(p => p.Length == 2)
                        .Select(p => p[1])
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new FeatureGroup(group.Key, enabledTools);
            });
    }

    private record FeatureGroup(string FeatureName, IReadOnlySet<string>? EnabledTools);
}