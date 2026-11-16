using Domain.Agents;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// What a conversation's reader is shown of it, and where its turns begin — the one rule the history's
// projection, a badge's count, a search and every rewrite read.
public static class Transcript
{
    // A message a reader is shown: what the transcript renders, what a badge counts and what a
    // conversation is searched by are all the same set. Role alone is not enough — a tool-using turn
    // persists assistant-role function calls with nothing to render, and a message carrying only files
    // must still survive. A call the person turned down reads as a line of the reply. Continue's ask is
    // nobody's message.
    public static bool IsShown(ChatMessage message) =>
        (message.Role == ChatRole.User || message.Role == ChatRole.Assistant || message.Role == ChatRole.Tool)
        && !message.IsContinuation()
        && (!string.IsNullOrWhiteSpace(TextOf(message)) || message.GetAttachments() is { Count: > 0 });

    // The messages of a line a reader is shown, in order. A reply that ran tools and ended on their
    // results without a word — cut by its person's steer, or stopped between calls — is still a reply:
    // the last of its results stands for it, so the tools it ran are drawn where it would have spoken.
    public static IReadOnlyList<ChatMessage> Shown(IReadOnlyList<ChatMessage> line) =>
        [.. line.Where((message, at) => IsShown(message) || EndsAReplyWithoutWords(line, at))];

    private static bool EndsAReplyWithoutWords(IReadOnlyList<ChatMessage> line, int at) =>
        line[at].Role == ChatRole.Tool
        && (at == line.Count - 1 || line[at + 1].Role == ChatRole.User)
        && line.Take(at).Reverse().TakeWhile(m => m.Role != ChatRole.User).All(m => !IsShown(m));

    public static string TextOf(ChatMessage message) =>
        message.Role == ChatRole.Tool
            ? string.Join("\n", message.Contents.OfType<FunctionResultContent>()
                .Select(c => ApprovalRejection.ToolOf(c.Result?.ToString()))
                .OfType<string>()
                .Select(ApprovalRejection.Line))
            : string.Join("", message.Contents.OfType<TextContent>().Select(c => c.Text));

    // Where a person's turn begins: their own message, never Continue's hidden ask, which carries on the
    // turn before it.
    public static bool StartsPersonsTurn(ChatMessage message) =>
        message.Role == ChatRole.User && !message.IsContinuation();
}