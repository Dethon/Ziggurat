using Domain.DTOs.Admin;

namespace Dashboard.Client.State.Households;

// How the last request to the household list came out, as the page says it: a refusal with the
// rules' own problems, or a sentence about why nothing could be asked.
public sealed record HouseholdsNotice(string Message, IReadOnlyList<string> Problems);

public sealed record HouseholdsState
{
    public IReadOnlyList<HouseholdListEntry> Households { get; init; } = [];

    public bool Loaded { get; init; }

    public bool Busy { get; init; }

    public HouseholdsNotice? Notice { get; init; }

    // The link just minted, shown until the next one replaces it or the page is left: it is never
    // asked for again, since only its hash is kept.
    public OwnerLink? ShownLink { get; init; }
}