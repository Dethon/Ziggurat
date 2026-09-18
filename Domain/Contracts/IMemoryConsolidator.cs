using Domain.DTOs;

namespace Domain.Contracts;

public interface IMemoryConsolidator
{
    Task<Consolidation> ConsolidateAsync(
        IReadOnlyList<MemoryEntry> memories, CancellationToken ct);

    Task<PersonalityProfile> SynthesizeProfileAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct);
}

// What one pass decided, and the sets within which it may be applied. A decision's sources have
// to sit together in one group — a cluster's linked component when Jev answered for it, the whole
// cluster as cosine made it when Jev did not — or the dreaming service refuses it: the first
// reason the code has ever had to say no to a destructive merge.
public record Consolidation(
    IReadOnlyList<MergeDecision> Decisions,
    IReadOnlyList<IReadOnlySet<string>> MergeableGroups)
{
    public static readonly Consolidation Empty = new([], []);

    public bool Permits(IReadOnlyCollection<string> sourceIds) =>
        MergeableGroups.Any(group => sourceIds.All(group.Contains));
}

public record MergeDecision(
    IReadOnlyList<string> SourceIds,
    MergeAction Action,
    string? MergedContent = null,
    MemoryCategory? Category = null,
    double? Importance = null,
    IReadOnlyList<string>? Tags = null);

public enum MergeAction
{
    Keep,
    Merge,
    SupersedeOlder
}