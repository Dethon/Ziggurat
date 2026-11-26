using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Domain.Conversations;

namespace Mcp.Hosting;

// The conversations a channel server opened, and so the only ones a follow-up may continue — and only
// the follow-ups of the owner that opened each. The agent would carry on any conversation id it is
// sent, and one it has never seen holds none of what the first turn said, so the answer would be to
// nothing; another owner's follow-up would carry on somebody else's conversation as their own turn.
// The owner is the server's to choose (a person, a person within one app) and is only compared.
//
// A conversation stays open for a window after its last turn and is forgotten after that. Kept in
// memory: a restart forgets them all, and the server's client starts afresh. What the first turn
// left to carry (TCarried) is handed back to each follow-up.
public sealed class OpenConversations<TOwner, TCarried>(TimeProvider clock, TimeSpan window)
    where TOwner : notnull
{
    private sealed record Entry(TOwner Owner, DateTimeOffset LastTurn, TCarried Carried);

    private readonly ConcurrentDictionary<string, Entry> _open = new();

    public string Open(TOwner owner, TCarried carried)
    {
        var now = clock.GetUtcNow();
        // Closed ones go when a conversation opens, so the table holds one window's at most.
        _open.Where(entry => !IsWithin(entry.Value.LastTurn, now))
            .Select(entry => entry.Key)
            .ToList()
            .ForEach(closed => _open.TryRemove(closed, out _));

        // Spelled as WebChat spells a conversation, so a review copy can be shown as a topic over this
        // very conversation and read the history the agent writes under it (ADR 0046).
        var conversation = ConversationIdGenerator.CreateFor(ConversationIdGenerator.NewTopicId()).ConversationId;
        _open[conversation] = new Entry(owner, now, carried);
        return conversation;
    }

    // False when this owner may not continue the conversation; if they may, the window starts again.
    public bool TryContinue(TOwner owner, string conversation, [MaybeNullWhen(false)] out TCarried carried)
    {
        var now = clock.GetUtcNow();
        if (!_open.TryGetValue(conversation, out var entry)
            || !EqualityComparer<TOwner>.Default.Equals(entry.Owner, owner)
            || !IsWithin(entry.LastTurn, now))
        {
            carried = default;
            return false;
        }

        _open[conversation] = entry with { LastTurn = now };
        carried = entry.Carried;
        return true;
    }

    private bool IsWithin(DateTimeOffset last, DateTimeOffset now) => now - last < window;
}