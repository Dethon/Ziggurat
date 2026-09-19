using System.Text.Json.Nodes;

namespace Domain.Judgments;

// What one call asks: the state the questions are about, and the questions keyed by the id
// their answers come back under. A question's instructions may name the state's fields.
//
// TurnModel is the model the turn behind the question asked for, as its config patch named it,
// and it is required on purpose: the client sends nothing for a turn addressed to the local box
// (a `lemonade/` id), and it can only hold that rule for every use if no use can ask without
// saying which turn it is asking for. It is passed down as plain data from wherever the turn is
// known — the message in the agent host, the call's `_meta` in a server — never read from an
// ambient. A question with no turn behind it says so with NoTurn.
public sealed record JudgmentRequest(
    JsonObject State,
    IReadOnlyDictionary<string, JudgmentQuestion> Questions,
    string? TurnModel)
{
    // No turn is behind this question (the nightly dreaming), or none that could have been
    // addressed to the local box (extraction, which such a turn is never enqueued for).
    public const string? NoTurn = null;
}

// The question kinds a use here has needed. A score is added by the first use that needs one.
public abstract record JudgmentQuestion(string Instructions);

// One of the criteria, by key, with a probability over all of them. The order of the criteria
// is the order the judge is shown them.
public sealed record ChoiceQuestion(
    string Instructions,
    IReadOnlyDictionary<string, string> Criteria) : JudgmentQuestion(Instructions);

// A yes or no, answered as the probability of yes.
public sealed record NoulQuestion(string Instructions) : JudgmentQuestion(Instructions);