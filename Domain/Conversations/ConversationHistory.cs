using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// A version a rewrite put aside: the line as it ran from its slot when it was put aside, and when that
// version was made, which orders it among its slot's others.
public sealed record HistoryVersion(DateTimeOffset MadeAt, IReadOnlyList<ChatMessage> Messages);

// What a slot holds besides the line shown: the versions put aside there, and when the one shown was made.
public sealed record SlotVersions(DateTimeOffset ActiveMadeAt, IReadOnlyList<HistoryVersion> Inactive)
{
    // Every version of the slot in the order a person flips through them, the one shown among them:
    // by when each was made, the shown one after any made at the same moment.
    public IReadOnlyList<HistoryVersion?> Ordered() =>
    [
        .. Inactive.Select(v => (HistoryVersion?)v)
            .Append(null)
            .Index()
            .OrderBy(x => x.Item?.MadeAt ?? ActiveMadeAt)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
    ];
}

public sealed record VersionPosition(int Index, int Count);

// The line the model and every page read, and the versions put aside at each place it branched, each
// kept by the entry id of the message its slot follows (HistoryRewrite.Root before the first).
public sealed record ConversationHistory(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyDictionary<string, SlotVersions> Versions)
{
    public VersionPosition? PositionAt(string slot) =>
        Versions.TryGetValue(slot, out var versions)
            ? new VersionPosition(versions.Ordered().ToList().IndexOf(null), versions.Inactive.Count + 1)
            : null;
}