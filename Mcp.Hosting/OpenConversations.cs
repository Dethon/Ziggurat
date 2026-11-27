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
//
// At most `ceiling` are kept. Every first turn opens one, and nothing but the window closes them, so a
// client opening them in a loop would grow the table without end; at the ceiling the one longest without
// a turn is forgotten, and its owner starts afresh as after the window, rather than anyone's new
// conversation being refused.
public sealed class OpenConversations<TOwner, TCarried>(TimeProvider clock, TimeSpan window, int ceiling)
    where TOwner : notnull
{
    private sealed record Entry(TOwner Owner, DateTimeOffset LastTurn, TCarried Carried);

    // One lock over both ways in: a follow-up's look-up and its window starting again are one step, so an
    // entry forgotten between them is never put back, and the ceiling holds exactly.
    private readonly int _ceiling = ceiling > 0 ? ceiling : throw new ArgumentOutOfRangeException(nameof(ceiling));

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _open = new(StringComparer.Ordinal);

    public string Open(TOwner owner, TCarried carried)
    {
        var now = clock.GetUtcNow();
        // Spelled as WebChat spells a conversation, so a review copy can be shown as a topic over this
        // very conversation and read the history the agent writes under it (ADR 0046).
        var conversation = ConversationIdGenerator.CreateFor(ConversationIdGenerator.NewTopicId()).ConversationId;
        lock (_lock)
        {
            _open.Where(entry => !IsWithin(entry.Value.LastTurn, now))
                .Select(entry => entry.Key)
                .ToList()
                .ForEach(closed => _open.Remove(closed));
            _open.OrderBy(entry => entry.Value.LastTurn)
                .Take(_open.Count - _ceiling + 1)
                .Select(entry => entry.Key)
                .ToList()
                .ForEach(oldest => _open.Remove(oldest));

            _open[conversation] = new Entry(owner, now, carried);
        }

        return conversation;
    }

    // False when this owner may not continue the conversation; if they may, the window starts again.
    public bool TryContinue(TOwner owner, string conversation, [MaybeNullWhen(false)] out TCarried carried)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (!_open.TryGetValue(conversation, out var entry))
            {
                carried = default;
                return false;
            }

            if (!IsWithin(entry.LastTurn, now))
            {
                _open.Remove(conversation);
                carried = default;
                return false;
            }

            if (!EqualityComparer<TOwner>.Default.Equals(entry.Owner, owner))
            {
                carried = default;
                return false;
            }

            _open[conversation] = entry with { LastTurn = now };
            carried = entry.Carried;
            return true;
        }
    }

    // A conversation opened for a turn that was never delivered: nothing is there to continue, and kept it
    // would hold a place under the ceiling that a client retrying could fill without end.
    public void Forget(string conversation)
    {
        lock (_lock)
        {
            _open.Remove(conversation);
        }
    }

    private bool IsWithin(DateTimeOffset last, DateTimeOffset now) => now - last < window;
}