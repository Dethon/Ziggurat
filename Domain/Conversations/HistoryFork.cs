using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// The copy a fork starts from, and the words it hands back to the composer.
public sealed record Fork(IReadOnlyList<ChatMessage> Messages, string? ComposerText);

// A fork from a reply copies the line through the end of that reply's turn; from a person's message,
// up to just before it, its words handed back to be sent again differently. The copy is a conversation
// of its own, so every message of it is named afresh; no version is copied.
public static class HistoryFork
{
    public static Fork? Of(IReadOnlyList<ChatMessage> messages, string entryId)
    {
        if (HistoryEntries.IndexOf(messages, entryId) is not { } at)
        {
            return null;
        }

        if (Transcript.StartsPersonsTurn(messages[at]))
        {
            return new Fork(Copied(messages.Take(at)), messages[at].Text);
        }

        var next = messages.Index().Skip(at + 1).FirstOrDefault(x => Transcript.StartsPersonsTurn(x.Item));
        return new Fork(Copied(next.Item is null ? messages : messages.Take(next.Index)), null);
    }

    private static IReadOnlyList<ChatMessage> Copied(IEnumerable<ChatMessage> messages) =>
        [.. messages.Select(m =>
        {
            var copy = m.Clone();
            copy.Contents = [.. m.Contents];
            copy.AdditionalProperties = m.AdditionalProperties is null ? [] : new(m.AdditionalProperties);
            copy.SetEntryId(Guid.NewGuid().ToString("N"));
            return copy;
        })];
}