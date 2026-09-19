using Domain.Channels;

namespace Domain.Judgments;

// The model the turn in flight asked for, as its config patch named it — what the judge's client
// reads to send nothing for a turn addressed to the local box. A server answering a tool call has
// it already: the call-tool filter entered the caller, and the caller carries it. The agent host
// asks outside any tool call, so the two places that do enter it here by hand, from the message
// they are judging. Nothing entered and no caller is no turn at all (the nightly dreaming).
public static class TurnModel
{
    private static readonly AsyncLocal<string?> _entered = new();

    public static string? Current => _entered.Value ?? CallerContext.Current?.ConfigPatchModel;

    public static IDisposable Enter(string? model)
    {
        var previous = _entered.Value;
        _entered.Value = model;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => _entered.Value = previous;
    }
}