using System.Collections.Concurrent;
using Domain.Conversations;
using Domain.DTOs.Channel;
using McpServerSugary.Settings;

namespace McpServerSugary.Channel;

// The conversations this server opened for a meal's first estimate, and so the only ones her
// answers may continue. The agent would carry on any conversation id it is sent, and one it has
// never seen holds neither her photos nor her words, so the answer would be an estimate of nothing.
// A conversation stays open for a window after its last turn and is forgotten after that. Kept in
// memory: a restart forgets them all, and the app then estimates the meal afresh.
public sealed class OpenEstimates(TimeProvider clock, SugarySettings settings)
{
    private sealed record Entry(DateTimeOffset LastTurn, ReviewCopy Review);

    private readonly ConcurrentDictionary<string, Entry> _open = new();

    private TimeSpan Window => TimeSpan.FromHours(settings.FollowUpHours);

    // The review copy the first turn asked for is kept with the conversation, because her answers
    // carry no words of the meal to name it by, and each of them asks for the same review again.
    public string Open(ReviewCopy review)
    {
        var now = clock.GetUtcNow();
        // Closed ones go when a meal starts, so the table holds the meals of one window at most.
        foreach (var closed in _open.Where(entry => !IsWithin(entry.Value.LastTurn, now)).Select(entry => entry.Key))
        {
            _open.TryRemove(closed, out _);
        }

        // Spelled as WebChat spells a conversation, so the review copy can be shown as a topic over
        // this very conversation and read the history the agent writes under it (ADR 0046).
        var conversation = ConversationIdGenerator.CreateFor(ConversationIdGenerator.NewTopicId()).ConversationId;
        _open[conversation] = new Entry(now, review);
        return conversation;
    }

    // The review copy her answers on this conversation ask for, or null when they may not continue
    // it; if they may, the window starts again.
    public ReviewCopy? TryContinue(string conversation)
    {
        var now = clock.GetUtcNow();
        if (!_open.TryGetValue(conversation, out var entry) || !IsWithin(entry.LastTurn, now))
        {
            return null;
        }

        _open[conversation] = entry with { LastTurn = now };
        return entry.Review;
    }

    private bool IsWithin(DateTimeOffset last, DateTimeOffset now) => now - last < Window;
}