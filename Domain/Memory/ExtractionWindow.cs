using Microsoft.Extensions.AI;

namespace Domain.Memory;

// The slice of conversation the memory extractor reads, and the turn markers it reads it by.
// Cutting and rendering live together so the marker vocabulary is written in one place, next
// to the prompt that names it (MemoryPrompts.ExtractionSystemPrompt).
//
// Neither half fetches. The caller has already read the persisted history and passes it in,
// which is what keeps both functions synchronous and free of dependencies.
public static class ExtractionWindow
{
    // The window is windowSize messages ending at the current turn: the fallback content is
    // that turn and claims the last slot, and the rest come from the history up to the anchor.
    // Everything persisted after the anchor is drift from turns that arrived later and is left
    // out — that is the anchor's whole purpose.
    //
    // The slots count conversation. A tool call and its result are persisted as two messages
    // that render as blank lines, and a turn that used two tools was the whole window with the
    // conversation before it gone. They are filtered after the cut, so the anchor keeps meaning
    // "messages persisted", and nothing is rendered in their place: the reply restates what was
    // fetched, and fetched text has no business in front of the memory writer.
    public static IReadOnlyList<ChatMessage> Build(
        IReadOnlyList<ChatMessage>? persistedHistory,
        MemoryAnchor anchor,
        string? fallbackContent,
        int windowSize)
    {
        var hasFallback = !string.IsNullOrEmpty(fallbackContent);
        var contextSlots = hasFallback ? windowSize - 1 : windowSize;

        var window = persistedHistory?
            .Take(anchor.PersistedMessageCount)
            .Where(IsConversation)
            .TakeLast(contextSlots)
            .ToList() ?? [];

        if (hasFallback)
        {
            window.Add(new ChatMessage(ChatRole.User, fallbackContent!));
        }

        return window;
    }

    public static string Render(IReadOnlyList<ChatMessage> window)
    {
        if (window.Count == 0)
        {
            return string.Empty;
        }

        var lastIndex = window.Count - 1;

        var groups = window
            .Take(lastIndex)
            .Select((msg, i) => Math.Max(1, window.Take(i + 1).Count(m => m.Role == ChatRole.User)))
            .ToArray();

        var maxGroup = groups.Length > 0 ? groups[lastIndex - 1] : 1;

        var lines = window.Select((msg, i) =>
        {
            if (i == lastIndex)
            {
                return $"[CURRENT]    {RoleLabel(msg.Role)}: {msg.Text}";
            }

            var offset = maxGroup - groups[i] + 1;
            return $"[context -{offset}] {RoleLabel(msg.Role)}: {msg.Text}";
        });

        return string.Join("\n", lines);
    }

    // Tool-role messages, and assistant messages that carry only a tool call. A user message is
    // always conversation, whatever it carries.
    private static bool IsConversation(ChatMessage message) =>
        message.Role != ChatRole.Tool
        && !(message.Role == ChatRole.Assistant && string.IsNullOrEmpty(message.Text));

    private static string RoleLabel(ChatRole role) =>
        role == ChatRole.Assistant ? "assistant" : "user";
}