using System.Text.Json.Serialization;
using Domain.DTOs.Tunnel;
using JetBrains.Annotations;

namespace Domain.Apps;

// What of the household a built app reaches: the mount points it reads, the outside origins it talks to
// and the servers its asks may use, in its manifest's words. An app space keeps the reach an owner
// approved (HouseholdSpace.Approved); while the app is shared, its published origin is granted what its
// served manifest asks Within that approval, and what it asks Beyond it waits on an owner.
[PublicAPI]
public sealed record AppReach
{
    public static readonly AppReach None = new();

    public IReadOnlyList<string> Reads { get; init; } = [];
    public IReadOnlyList<string> Connect { get; init; } = [];

    // Servers by the names they are configured under (AppManifest.Asks). A section saved before approvals
    // named servers reads as approving none.
    public IReadOnlyList<string> Asks { get; init; } = [];

    public static AppReach Of(AppManifest manifest) =>
        new() { Reads = manifest.Reads, Connect = manifest.Connect, Asks = manifest.Asks };

    [JsonIgnore]
    public bool IsEmpty => Reads.Count == 0 && Connect.Count == 0 && Asks.Count == 0;

    // What of this reach the approval covers, in this reach's order. No approval covers nothing.
    public AppReach Within(AppReach? approved) => new()
    {
        Reads = Covered(Reads, approved?.Reads, true),
        Connect = Covered(Connect, approved?.Connect, true),
        Asks = Covered(Asks, approved?.Asks, true)
    };

    // What of this reach the approval does not cover: what waits on an owner.
    public AppReach Beyond(AppReach? approved) => new()
    {
        Reads = Covered(Reads, approved?.Reads, false),
        Connect = Covered(Connect, approved?.Connect, false),
        Asks = Covered(Asks, approved?.Asks, false)
    };

    // Two reaches are equal when they name the same things in the same order: a section read back is the
    // section saved.
    public bool Equals(AppReach? other) =>
        other is not null && Reads.SequenceEqual(other.Reads, StringComparer.Ordinal)
                          && Connect.SequenceEqual(other.Connect, StringComparer.Ordinal)
                          && Asks.SequenceEqual(other.Asks, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(string.Join('\n', Reads), string.Join('\n', Connect), string.Join('\n', Asks));

    // Its manifest's rules for each entry's shape, named as the section names a space.
    public IEnumerable<string> Problems(string slug) =>
        Reads.Where(read => !AppManifest.IsMountPoint(read))
            .Select(read => $"space {slug}'s approved reads: '{read}' is no mount point")
            .Concat(Connect.Where(origin => !AppManifest.IsBareHttpsOrigin(origin))
                .Select(origin => $"space {slug}'s approved connect: '{origin}' is no bare https origin"))
            .Concat(Asks.Where(server => !TunnelCredentialProtocol.IsServerName(server))
                .Select(server => $"space {slug}'s approved asks: '{server}' is no server's name"));

    private static IReadOnlyList<string> Covered(IReadOnlyList<string> asked, IReadOnlyList<string>? approved, bool covered) =>
        [.. asked.Where(entry => (approved?.Contains(entry, StringComparer.Ordinal) ?? false) == covered)];
}