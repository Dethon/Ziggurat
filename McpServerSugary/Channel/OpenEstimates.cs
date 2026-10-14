using System.Collections.Concurrent;
using McpServerSugary.Settings;

namespace McpServerSugary.Channel;

// The conversations this server opened for a meal's first estimate, and so the only ones her
// answers may continue. The agent would carry on any conversation id it is sent, and one it has
// never seen holds neither her photos nor her words, so the answer would be an estimate of nothing.
// A conversation stays open for a window after its last turn and is forgotten after that. Kept in
// memory: a restart forgets them all, and the app then estimates the meal afresh.
public sealed class OpenEstimates(TimeProvider clock, SugarySettings settings)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTurn = new();

    private TimeSpan Window => TimeSpan.FromHours(settings.FollowUpHours);

    public string Open()
    {
        var now = clock.GetUtcNow();
        // Closed ones go when a meal starts, so the table holds the meals of one window at most.
        foreach (var closed in _lastTurn.Where(entry => !IsWithin(entry.Value, now)).Select(entry => entry.Key))
        {
            _lastTurn.TryRemove(closed, out _);
        }

        var conversation = Guid.NewGuid().ToString("N");
        _lastTurn[conversation] = now;
        return conversation;
    }

    // Whether her answers may continue this conversation; if they may, the window starts again.
    public bool TryContinue(string conversation)
    {
        var now = clock.GetUtcNow();
        if (!_lastTurn.TryGetValue(conversation, out var last) || !IsWithin(last, now))
        {
            return false;
        }

        _lastTurn[conversation] = now;
        return true;
    }

    private bool IsWithin(DateTimeOffset last, DateTimeOffset now) => now - last < Window;
}