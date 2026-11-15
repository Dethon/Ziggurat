using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

public enum RewriteRefusal
{
    // The line is not what the page last saw: a turn or another page wrote to it since.
    Changed,

    // The message named is not on the line.
    NotFound,

    // A turn is running on the conversation, and would write over whatever the rewrite left.
    TurnInFlight
}

public abstract record RewriteOutcome;

public sealed record Rewritten(ConversationHistory Before, ConversationHistory After) : RewriteOutcome;

public sealed record RewriteRefused(RewriteRefusal Reason) : RewriteOutcome;

// A change to a conversation's line a person asked for, named in the entry ids the page saw (legacy ones
// included), with the entry id of the last message the page shows: a line that moved on since is not
// rewritten from a stale view of it.
public abstract record HistoryRewrite(string? LastSeen)
{
    public const string Root = "root";

    public static RewriteOutcome Apply(ConversationHistory history, HistoryRewrite rewrite, DateTimeOffset now)
    {
        var lastSeen = history.Messages.Count == 0 ? null : HistoryEntries.IdAt(history.Messages, history.Messages.Count - 1);
        if (lastSeen != rewrite.LastSeen)
        {
            return new RewriteRefused(RewriteRefusal.Changed);
        }

        return rewrite.Resolve(history.Messages) is { } at
            ? new Rewritten(history, rewrite.Make(Stamped(history), at, now))
            : new RewriteRefused(RewriteRefusal.NotFound);
    }

    // Where on the line the rewrite acts, as an index, before anything is stamped: a legacy name is a
    // place and a fingerprint, which only the line as the page saw it answers.
    private protected abstract int? Resolve(IReadOnlyList<ChatMessage> messages);

    private protected abstract ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now);

    // The slot after the message at an index; -1 is the root.
    private protected static string SlotAfter(IReadOnlyList<ChatMessage> messages, int index) =>
        index < 0 ? Root : messages[index].GetEntryId()!;

    private protected static int? SlotIndex(IReadOnlyList<ChatMessage> messages, string? slotEntryId) =>
        slotEntryId is null or Root ? -1 : HistoryEntries.IndexOf(messages, slotEntryId);

    private protected static Dictionary<string, SlotVersions> Copy(ConversationHistory history) =>
        new(history.Versions);

    // A line kept before entry ids gets real ones as it is first rewritten, so what is put aside can be
    // found by them again. Copies: the history read is the caller's.
    private static ConversationHistory Stamped(ConversationHistory history) =>
        history.Messages.All(m => m.GetEntryId() is not null)
            ? history
            : history with
            {
                Messages = [.. history.Messages.Select(m => m.GetEntryId() is not null ? m : WithEntryId(m))]
            };

    private static ChatMessage WithEntryId(ChatMessage message)
    {
        var copy = message.Clone();
        copy.AdditionalProperties = message.AdditionalProperties is null ? [] : new(message.AdditionalProperties);
        copy.SetEntryId(Guid.NewGuid().ToString("N"));
        return copy;
    }

    // Removes the versions kept under any entry of what leaves, and under any entry of those versions.
    private protected static void Forget(Dictionary<string, SlotVersions> versions, IEnumerable<ChatMessage> leaving)
    {
        var ids = new Queue<string>(leaving.Select(m => m.GetEntryId()).OfType<string>());
        while (ids.TryDequeue(out var id))
        {
            if (!versions.Remove(id, out var slot))
            {
                continue;
            }

            foreach (var inner in slot.Inactive.SelectMany(v => v.Messages).Select(m => m.GetEntryId()).OfType<string>())
            {
                ids.Enqueue(inner);
            }
        }
    }
}

// The line from a slot on is kept as a version of that slot, and the line ends there: what a reroll or an
// edit-and-re-run does before its turn writes the new version.
public sealed record PutAside(string? AfterEntryId, string? LastSeen) : HistoryRewrite(LastSeen)
{
    private protected override int? Resolve(IReadOnlyList<ChatMessage> messages) => SlotIndex(messages, AfterEntryId);

    private protected override ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now)
    {
        var slot = SlotAfter(history.Messages, at);
        var tail = history.Messages.Skip(at + 1).ToList();
        var versions = Copy(history);
        var existing = versions.GetValueOrDefault(slot);

        // A line from before any rewrite was made when it was said; one a rewrite made, when that was.
        var madeAt = existing?.ActiveMadeAt ?? tail.FirstOrDefault()?.GetTimestamp() ?? DateTimeOffset.MinValue;
        IReadOnlyList<HistoryVersion> inactive = tail.Count == 0
            ? existing?.Inactive ?? []
            : [.. existing?.Inactive ?? [], new HistoryVersion(madeAt, tail)];
        versions[slot] = new SlotVersions(now, inactive);

        return new ConversationHistory([.. history.Messages.Take(at + 1)], versions);
    }
}

// The slot shows another of its versions, by its place in the order a person flips through them.
public sealed record SwitchVersion(string? SlotEntryId, int Index, string? LastSeen) : HistoryRewrite(LastSeen)
{
    private protected override int? Resolve(IReadOnlyList<ChatMessage> messages) => SlotIndex(messages, SlotEntryId);

    private protected override ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now)
    {
        var slot = SlotAfter(history.Messages, at);
        if (!history.Versions.TryGetValue(slot, out var current)
            || Index < 0
            || current.Ordered() is not { } ordered
            || Index >= ordered.Count
            || ordered[Index] is not { } chosen)
        {
            return history;
        }

        var tail = history.Messages.Skip(at + 1).ToList();
        IReadOnlyList<HistoryVersion> inactive =
        [
            .. current.Inactive.Where(v => !ReferenceEquals(v, chosen)),
            .. tail.Count == 0 ? [] : new[] { new HistoryVersion(current.ActiveMadeAt, tail) }
        ];
        var versions = Copy(history);
        if (inactive.Count == 0)
        {
            versions.Remove(slot);
        }
        else
        {
            versions[slot] = new SlotVersions(chosen.MadeAt, inactive);
        }

        return new ConversationHistory([.. history.Messages.Take(at + 1), .. chosen.Messages], versions);
    }
}

// A message and everything after it on the line leave it, with the versions kept further down. Where its
// slot holds other versions the newest of them is shown in its place, so deleting the version shown
// never deletes the others.
public sealed record CutFrom(string EntryId, string? LastSeen) : HistoryRewrite(LastSeen)
{
    private protected override int? Resolve(IReadOnlyList<ChatMessage> messages) => HistoryEntries.IndexOf(messages, EntryId);

    private protected override ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now)
    {
        var slot = SlotAfter(history.Messages, at - 1);
        var versions = Copy(history);
        Forget(versions, history.Messages.Skip(at));

        var kept = history.Messages.Take(at).ToList();
        if (!versions.TryGetValue(slot, out var current) || current.Inactive.Count == 0)
        {
            versions.Remove(slot);
            return new ConversationHistory(kept, versions);
        }

        var newest = current.Inactive.Index().MaxBy(x => (x.Item.MadeAt, x.Index)).Item;
        var rest = current.Inactive.Where(v => !ReferenceEquals(v, newest)).ToList();
        if (rest.Count == 0)
        {
            versions.Remove(slot);
        }
        else
        {
            versions[slot] = new SlotVersions(newest.MadeAt, rest);
        }

        return new ConversationHistory([.. kept, .. newest.Messages], versions);
    }
}