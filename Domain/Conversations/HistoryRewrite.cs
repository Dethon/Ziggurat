using Domain.DTOs.Channel;
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
    TurnInFlight,

    // Only a person's own message is run again: not a reply, and not a turn a service started.
    NotRerunnable
}

public abstract record RewriteOutcome;

public sealed record Rewritten(ConversationHistory Before, ConversationHistory After) : RewriteOutcome
{
    // The files the conversation held before and holds nowhere now, on its line or in a version.
    public IReadOnlyList<string> AttachmentsNoLongerHeld() => [.. Before.HeldAttachments().Except(After.HeldAttachments())];
}

public sealed record RewriteRefused(RewriteRefusal Reason) : RewriteOutcome;

// A change to a conversation's line a person asked for, named in the entry ids the page saw (legacy ones
// included), with the entry id of the last message the page shows: a line that moved on since is not
// rewritten from a stale view of it.
public abstract record HistoryRewrite(string? LastSeen)
{
    public const string Root = "root";

    public static RewriteOutcome Apply(ConversationHistory history, HistoryRewrite rewrite, DateTimeOffset now)
    {
        // The last message a page could have named: what the line ends on may be shown to nobody.
        var lastShown = Transcript.Shown(history.Messages).LastOrDefault();
        var shown = history.Messages.Index().LastOrDefault(x => ReferenceEquals(x.Item, lastShown));
        var lastSeen = shown.Item is null ? null : HistoryEntries.IdAt(history.Messages, shown.Index);
        if (lastSeen != rewrite.LastSeen)
        {
            return new RewriteRefused(RewriteRefusal.Changed);
        }

        if (rewrite.Resolve(history.Messages) is not { } at)
        {
            return new RewriteRefused(RewriteRefusal.NotFound);
        }

        return rewrite.Refusal(history.Messages, at) is { } refusal
            ? new RewriteRefused(refusal)
            : new Rewritten(history, FailedTurnsWhole(rewrite.Make(Stamped(history), at, now)));
    }

    // A failed turn is the line's last: the person's words and what they were told instead of an answer,
    // which a retry replaces. A mark a rewrite left anywhere else — under a reply a version brought back,
    // or on words whose error was deleted — would hide a message from the model that no retry can reach,
    // so it comes off; the words are then a message like any other.
    private static ConversationHistory FailedTurnsWhole(ConversationHistory history)
    {
        var messages = history.Messages;
        var tail = messages.Reverse().TakeWhile(m => m.IsFailedTurn()).Count();
        var keepTail = tail > 0 && messages[^1].Role == ChatRole.Assistant;
        var kept = keepTail ? messages.Count - tail : messages.Count;
        return messages.Take(kept).Any(m => m.IsFailedTurn())
            ? history with { Messages = [.. messages.Index().Select(x => x.Index < kept && x.Item.IsFailedTurn() ? Answered(x.Item) : x.Item)] }
            : history;
    }

    private static ChatMessage Answered(ChatMessage message)
    {
        var copy = message.Clone();
        copy.AdditionalProperties = message.AdditionalProperties is null ? [] : new(message.AdditionalProperties);
        copy.UnmarkFailedTurn();
        return copy;
    }

    // Where on the line the rewrite acts, as an index, before anything is stamped: a legacy name is a
    // place and a fingerprint, which only the line as the page saw it answers.
    private protected abstract int? Resolve(IReadOnlyList<ChatMessage> messages);

    private protected abstract ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now);

    // Why the message found may not be rewritten this way, where there is a reason.
    private protected virtual RewriteRefusal? Refusal(IReadOnlyList<ChatMessage> messages, int at) => null;

    // A person's own words, which is all a page may send again as its person: never a reply, and never
    // a turn a service started, which acted for whoever its delegation named.
    private protected static bool IsPersonsMessage(ChatMessage message) =>
        Transcript.StartsPersonsTurn(message)
        && message.GetConversationContext()?.OriginKind is null
        && !ServiceSenders.IsService(message.GetSenderId() ?? "")
        && message.GetResponseFormat() is null;

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

    // A copy under a new entry id, its contents and properties its own.
    private protected static ChatMessage WithEntryId(ChatMessage message, IEnumerable<AIContent>? contents = null)
    {
        var copy = message.Clone();
        copy.Contents = [.. contents ?? message.Contents];
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

    private protected override ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now) =>
        Before(history, at + 1, now);

    // The line from an index on put aside as a version of the slot before it.
    internal static ConversationHistory Before(ConversationHistory history, int from, DateTimeOffset now)
    {
        var at = from - 1;
        var slot = SlotAfter(history.Messages, at);

        // A failed turn is replaced by its retry, never kept as a version.
        var tail = history.Messages.Skip(at + 1).Where(m => !m.IsFailedTurn()).ToList();
        var versions = Copy(history);
        var existing = versions.GetValueOrDefault(slot);

        // A line from before any rewrite was made when it was said; one a rewrite made, when that was.
        var madeAt = existing?.ActiveMadeAt ?? tail.FirstOrDefault()?.GetTimestamp() ?? DateTimeOffset.MinValue;
        IReadOnlyList<HistoryVersion> inactive = tail.Count == 0
            ? existing?.Inactive ?? []
            : [.. existing?.Inactive ?? [], new HistoryVersion(madeAt, tail)];
        // A slot with nothing put aside has one version, which is no choice to draw.
        if (inactive.Count == 0)
        {
            versions.Remove(slot);
        }
        else
        {
            // What replaces the versions is made after them, whatever the clock says: a host that set
            // its clock back between a reply and its reroll dated the reply after this moment, and the
            // slot sorted the new version first.
            var latest = inactive.Max(v => v.MadeAt);
            versions[slot] = new SlotVersions(now > latest ? now : latest.AddTicks(1), inactive);
        }

        return new ConversationHistory([.. history.Messages.Take(at + 1)], versions);
    }
}

// The person's message and everything after it are kept as a version of the slot before it, and the
// line ends there: what an edit-and-re-run does before the edited words are sent as a turn.
public sealed record PutAsideFrom(string EntryId, string? LastSeen) : HistoryRewrite(LastSeen)
{
    private protected override int? Resolve(IReadOnlyList<ChatMessage> messages) => HistoryEntries.IndexOf(messages, EntryId);

    private protected override RewriteRefusal? Refusal(IReadOnlyList<ChatMessage> messages, int at) =>
        IsPersonsMessage(messages[at]) ? null : RewriteRefusal.NotRerunnable;

    private protected override ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now) =>
        PutAside.Before(history, at, now);
}

// A message corrected in place, no turn run: the old line from it on is kept as a version of the slot
// before it, and the line goes on from the corrected message with what followed copied under new entry
// ids (the versions further down stay with the old line, and come back with it). A reply is corrected
// as the one bubble it is drawn as: its turn's tool calls and results stay paired, what the model said
// along the way goes, and the corrected text is the turn's last assistant word.
public sealed record EditInPlace(string EntryId, string Text, string? LastSeen) : HistoryRewrite(LastSeen)
{
    private protected override int? Resolve(IReadOnlyList<ChatMessage> messages) => HistoryEntries.IndexOf(messages, EntryId);

    private protected override ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now)
    {
        var messages = history.Messages;
        var (from, to) = Transcript.StartsPersonsTurn(messages[at]) ? (at, at) : TurnAround(messages, at);
        var corrected = Transcript.StartsPersonsTurn(messages[at])
            ? [WithEntryId(messages[at], Replaced(messages[at].Contents))]
            : Reply(messages.Skip(from).Take(to - from + 1).ToList());
        var aside = PutAside.Before(history, from, now);
        return aside with
        {
            Messages = [.. aside.Messages, .. corrected, .. messages.Skip(to + 1).Select(m => WithEntryId(m))]
        };
    }

    // The reply's turn: from the first message after the person's to the last before the next person's.
    private static (int From, int To) TurnAround(IReadOnlyList<ChatMessage> messages, int at)
    {
        var from = messages.Take(at).Index().LastOrDefault(x => Transcript.StartsPersonsTurn(x.Item)) is { Item: not null } user
            ? user.Index + 1
            : 0;
        var next = messages.Index().Skip(at + 1).FirstOrDefault(x => Transcript.StartsPersonsTurn(x.Item));
        return (from, next.Item is null ? messages.Count - 1 : next.Index - 1);
    }

    private IReadOnlyList<ChatMessage> Reply(IReadOnlyList<ChatMessage> turn)
    {
        // Continue's asks go with the words they asked for: the corrected reply is one reply.
        turn = [.. turn.Where(m => !m.IsContinuation())];
        var last = turn.Index().LastOrDefault(x => x.Item.Role == ChatRole.Assistant).Index;
        var copies = turn.Index().Select(x => x.Item.Role != ChatRole.Assistant
            ? WithEntryId(x.Item)
            : x.Index == last
                ? WithEntryId(x.Item, Replaced(x.Item.Contents))
                : WithEntryId(x.Item, Unsaid(x.Item.Contents)));
        return [.. copies.Where(m => m.Contents.Count > 0)];
    }

    private IEnumerable<AIContent> Replaced(IEnumerable<AIContent> contents) =>
        [new TextContent(Text), .. Unsaid(contents)];

    // What the model said goes; its reasoning stays beside a tool call it led to, since a provider may
    // refuse a call whose reasoning was taken away.
    private static IEnumerable<AIContent> Unsaid(IEnumerable<AIContent> contents)
    {
        var list = contents.ToList();
        var calls = list.Any(c => c is FunctionCallContent);
        return list.Where(c => c is not TextContent && (calls || c is not TextReasoningContent));
    }
}

// A reroll, before the turn that answers the person's message again: the reply after it is kept as a
// version of the slot after it, and the line ends on the message.
public sealed record PutAsideReply(string UserEntryId, string? LastSeen) : HistoryRewrite(LastSeen)
{
    private protected override int? Resolve(IReadOnlyList<ChatMessage> messages) => HistoryEntries.IndexOf(messages, UserEntryId);

    private protected override RewriteRefusal? Refusal(IReadOnlyList<ChatMessage> messages, int at) =>
        IsPersonsMessage(messages[at]) ? null : RewriteRefusal.NotRerunnable;

    private protected override ConversationHistory Make(ConversationHistory history, int at, DateTimeOffset now) =>
        PutAside.Before(history, at + 1, now);
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

        // A failed turn is replaced, never kept as a version.
        var tail = history.Messages.Skip(at + 1).Where(m => !m.IsFailedTurn()).ToList();
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
    // A reply is one bubble over its whole turn, so it is cut from the first message after the person's.
    private protected override int? Resolve(IReadOnlyList<ChatMessage> messages) =>
        HistoryEntries.IndexOf(messages, EntryId) is not { } index
            ? null
            : Transcript.StartsPersonsTurn(messages[index])
                ? index
                : messages.Take(index).Index().LastOrDefault(x => Transcript.StartsPersonsTurn(x.Item)) is { Item: not null } user
                    ? user.Index + 1
                    : 0;

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