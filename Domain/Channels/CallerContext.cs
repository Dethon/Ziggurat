using Domain.DTOs.Channel;

namespace Domain.Channels;

// The conversation a tool call belongs to, for code that answers a tool call without seeing the
// request: a filesystem backend's operations take a path and a payload, never the `_meta` the agent
// stamped on the call. The call-tool filter every server installs enters the context the request
// carries for the duration of the call, so a backend that needs the caller — the Home Assistant
// mount, whose watch records the agent that created it — asks here rather than growing a context
// parameter through thirteen operations nothing else needs it on.
//
// Absent means the call carried no context (a harness, a benchmark), and a consumer refuses rather
// than guesses — the same rule ConversationScope states for the tool servers that read _meta.
public static class CallerContext
{
    private static readonly AsyncLocal<ConversationContext?> _current = new();

    public static ConversationContext? Current => _current.Value;

    public static IDisposable Enter(ConversationContext? context)
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Scope(previous);
    }

    private sealed class Scope(ConversationContext? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}