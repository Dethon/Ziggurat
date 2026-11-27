using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// What each turn did on its way to its words — its reasoning, and every tool call with its answer — hung
// on the messages a reader is shown (Transcript.Shown), which a page draws, counts and rewrites by: a
// turn's calls are kept as messages of their own, but those are no messages to a reader. A step goes on
// the next words of its turn (Before), and what came after a turn's last words (cut by a steer or a stop
// before it spoke again) on those words (After). The steps of a turn with no words at all go on the
// result that stands for its reply. A call's answer is found by its id anywhere in the line, so a call
// is one step however its result was stored.
public static class TranscriptSteps
{
    private static readonly IEqualityComparer<ChatMessage> _byReference = ReferenceEqualityComparer.Instance;

    public sealed record Placed(IReadOnlyList<TurnStep>? Before, IReadOnlyList<TurnStep>? After);

    public static IReadOnlyDictionary<ChatMessage, Placed> Of(IReadOnlyList<ChatMessage> line)
    {
        var shown = Transcript.Shown(line).ToHashSet(_byReference);
        var answers = line.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .GroupBy(r => r.CallId)
            .ToDictionary(g => g.Key, g => ToolSteps.Result(g.Last()));

        var before = new Dictionary<ChatMessage, IReadOnlyList<TurnStep>>(_byReference);
        var after = new Dictionary<ChatMessage, IReadOnlyList<TurnStep>>(_byReference);
        List<TurnStep> pending = [];
        ChatMessage? lastWords = null;

        void Settle()
        {
            if (pending.Count > 0 && lastWords is not null)
            {
                after[lastWords] = [.. pending];
            }

            pending = [];
            lastWords = null;
        }

        foreach (var message in line)
        {
            if (message.Role == ChatRole.User)
            {
                Settle();
                continue;
            }

            if (ReasoningOf(message) is { } reasoning)
            {
                pending.Add(new TurnStep(Reasoning: reasoning));
            }

            var calls = message.Contents.OfType<FunctionCallContent>()
                .Select(call => new TurnStep(Tool: answers.TryGetValue(call.CallId, out var answer)
                    ? ToolSteps.Call(call).Merge(answer)
                    : ToolSteps.Call(call)))
                .ToList();

            if (shown.Contains(message))
            {
                if (pending.Count > 0)
                {
                    before[message] = [.. pending];
                }

                pending = [];
                lastWords = message;
            }

            pending.AddRange(calls);
        }

        Settle();
        return before.Keys.Concat(after.Keys)
            .Distinct(_byReference)
            .ToDictionary(
                m => m,
                m => new Placed(before.GetValueOrDefault(m), after.GetValueOrDefault(m)),
                _byReference);
    }

    // A provider streams reasoning a token at a time, kept as one part each.
    private static string? ReasoningOf(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant)
        {
            return null;
        }

        var text = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
        return string.IsNullOrWhiteSpace(text) ? null : ToolSteps.Reasoning(text);
    }
}