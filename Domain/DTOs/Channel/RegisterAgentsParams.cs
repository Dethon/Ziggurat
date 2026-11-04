using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record RegisterAgentsParams
{
    public required IReadOnlyList<AgentCatalogEntry> Agents { get; init; }

    // Each household's own Lemonade box's models now, by household (the home's its own box's or the
    // deployment's): what its picker offers beside the configured models. A channel that shows no
    // picker does not declare it, and so never reads it.
    public IReadOnlyDictionary<string, IReadOnlyList<PatchableModel>>? HouseholdLemonadeModels { get; init; }
}