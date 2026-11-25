using JetBrains.Annotations;

namespace Domain.Apps;

// What of the household a built app reaches: the mount points it reads and the outside origins it talks
// to, in its manifest's words. An app space keeps the reach an owner approved (HouseholdSpace.Approved);
// while the app is shared, its published origin is granted what its served manifest asks Within that
// approval, and what it asks Beyond it waits on an owner.
[PublicAPI]
public sealed record AppReach
{
    public static readonly AppReach None = new();

    public IReadOnlyList<string> Reads { get; init; } = [];
    public IReadOnlyList<string> Connect { get; init; } = [];

    public static AppReach Of(AppManifest manifest) => new() { Reads = manifest.Reads, Connect = manifest.Connect };

    public bool IsEmpty => Reads.Count == 0 && Connect.Count == 0;

    // What of this reach the approval covers, in this reach's order. No approval covers nothing.
    public AppReach Within(AppReach? approved) => new()
    {
        Reads = [.. Reads.Where(read => approved?.Reads.Contains(read, StringComparer.Ordinal) == true)],
        Connect = [.. Connect.Where(origin => approved?.Connect.Contains(origin, StringComparer.Ordinal) == true)]
    };

    // What of this reach the approval does not cover: what waits on an owner.
    public AppReach Beyond(AppReach? approved) => new()
    {
        Reads = [.. Reads.Where(read => approved?.Reads.Contains(read, StringComparer.Ordinal) != true)],
        Connect = [.. Connect.Where(origin => approved?.Connect.Contains(origin, StringComparer.Ordinal) != true)]
    };

    // Two reaches are equal when they name the same things in the same order: a section read back is the
    // section saved.
    public bool Equals(AppReach? other) =>
        other is not null && Reads.SequenceEqual(other.Reads, StringComparer.Ordinal)
                          && Connect.SequenceEqual(other.Connect, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(string.Join('\n', Reads), string.Join('\n', Connect));

    // Its manifest's rules for each entry's shape, named as the section names a space.
    public IEnumerable<string> Problems(string slug) =>
        Reads.Where(read => !AppManifest.IsMountPoint(read))
            .Select(read => $"space {slug}'s approved reads: '{read}' is no mount point")
            .Concat(Connect.Where(origin => !AppManifest.IsBareHttpsOrigin(origin))
                .Select(origin => $"space {slug}'s approved connect: '{origin}' is no bare https origin"));
}