using Domain.Channels;

namespace Domain.Agents;

// The model the turn in flight asked for, as its config patch named it and before any host was
// chosen — beside LemonadeModelId, which is what a reader asks of it. It is a fact about the turn,
// not about any one reader: today the judge's client reads it to send nothing for a turn
// addressed to the local box, and anything else that must behave differently on such a turn
// reads it here rather than growing a parameter.
//
// Ambient because its readers are singletons shared by every turn running at once and sit below
// code that knows nothing of turns: an AsyncLocal flows down one turn's own call chain and never
// into a sibling's, the way CallerContext does. A server answering a tool call has it already —
// the call-tool filter entered the caller, and the caller carries it. The agent host asks outside
// any tool call, so code there enters it by hand from the message it is working on. Nothing
// entered and no caller is no turn at all (the nightly dreaming).
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