using System.Text.Json.Nodes;

namespace Domain.Judgments;

// What one call asks: the state the questions are about, and the questions keyed by the id
// their answers come back under. A question's instructions may name the state's fields.
public sealed record JudgmentRequest(
    JsonObject State,
    IReadOnlyDictionary<string, JudgmentQuestion> Questions);

// The question kinds a use here has needed. A score is added by the first use that needs one.
public abstract record JudgmentQuestion(string Instructions);

// One of the criteria, by key, with a probability over all of them. The order of the criteria
// is the order the judge is shown them.
public sealed record ChoiceQuestion(
    string Instructions,
    IReadOnlyDictionary<string, string> Criteria) : JudgmentQuestion(Instructions);

// A yes or no, answered as the probability of yes.
public sealed record NoulQuestion(string Instructions) : JudgmentQuestion(Instructions);