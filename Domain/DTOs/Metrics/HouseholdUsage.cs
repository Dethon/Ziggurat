using JetBrains.Annotations;

namespace Domain.DTOs.Metrics;

// What one household used over a range, as its owner reads it: tokens and cost per agent and per
// person, and what it asked of the shared Lemonade. Read from the household's own roll-ups alone.
[PublicAPI]
public sealed record HouseholdUsage(IReadOnlyList<UsageLine> Agents, IReadOnlyList<UsageLine> People, LemonadeUsage Lemonade)
{
    // The Observability host's one route that is not the operator's: asked by the accounts service's
    // admin API alone, with an assertion it signs for the owner on AppChannels.Usage, and answering
    // the household that assertion names — the route takes none.
    public const string Path = "/api/usage";
}

// An agent (under the name its latest event went by) or a person (under the name their turns carried).
[PublicAPI]
public sealed record UsageLine(string Name, long Tokens, decimal Cost);

// What a household asked of the shared Lemonade over a range, each in its own unit.
[PublicAPI]
public sealed record LemonadeUsage(double TranscribedSeconds, long SpokenCharacters, long Embeddings);