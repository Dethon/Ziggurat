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

// An agent (under the name its latest event went by) or a person: keyed by their account (Id) and
// shown under the latest name their turns carried, which the accounts service replaces with the name
// the household knows them by now. A person's line with no Id is a call from before accounts were
// carried, under the name it went by.
[PublicAPI]
public sealed record UsageLine(string Name, long Tokens, decimal Cost, string? Id = null);

// What a household asked of the shared Lemonade over a range, each in its own unit.
[PublicAPI]
public sealed record LemonadeUsage(double TranscribedSeconds, long SpokenCharacters, long Embeddings);