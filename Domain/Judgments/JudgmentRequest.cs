using System.Text.Json.Nodes;
using Domain.DTOs.Channel;

namespace Domain.Judgments;

// What one call asks: the state the questions are about, the questions keyed by the id their
// answers come back under, and who is asking. A question's instructions may name the state's
// fields.
public sealed record JudgmentRequest(
    JsonObject State,
    IReadOnlyDictionary<string, JudgmentQuestion> Questions,
    JudgmentCaller Caller);

// Who a judgment is asked for, required on purpose and passed down as plain data from wherever
// the turn is known — the message in the agent host, the call's `_meta` in a server — never read
// from an ambient. Two rules hang on it, and the client holds both for every use, the ones
// written and the ones not yet:
//
// - TurnModel is the model the turn behind the question asked for, as its config patch named
//   it. The client sends nothing for a turn addressed to the local box (a `lemonade/` id). Null
//   is no patch, or no turn at all (the nightly dreaming, extraction).
// - Sender, AgentId and ConversationId are whom the judgment's usage is billed to, the way a chat
//   turn's is: a judgment costs money like any model call, and its cost lands on the same
//   figures. Any of them may be unknown; Sender is then "unknown", as a chat turn's is.
public sealed record JudgmentCaller(
    string? TurnModel,
    string? Sender,
    string? AgentId = null,
    string? ConversationId = null)
{
    // Nobody and no turn: a question with nothing behind it to bill.
    public static readonly JudgmentCaller None = new(null, null);

    // The conversation a server's tool call came from, as its `_meta` carried it; none is a call
    // with nothing behind it (a fixture, a harness).
    public static JudgmentCaller For(ConversationContext? conversation) => conversation is null
        ? None
        : new(conversation.ConfigPatchModel, conversation.UserId, conversation.AgentId, conversation.ConversationId);
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