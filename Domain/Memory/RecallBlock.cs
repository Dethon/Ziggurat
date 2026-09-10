using Domain.DTOs;

namespace Domain.Memory;

// The decoration that carries remembered facts to the model. MemoryPrompts.FeatureSystemPrompt
// tells the model to look for this block by name, so the text lives next to the promise.
//
// It owns the text and not when the block is applied: the block must land on the copy sent to
// the model and never on the copy that gets persisted. See
// docs/adr/0010-every-user-turn-carries-its-own-recall-block.md.
public static class RecallBlock
{
    // The forget rule, once more at the foot of the block. The system prompt states it hundreds
    // of lines above the conversation, and a model reading "ya he vuelto de Lisboa" over a stored
    // trip-in-preparation still answered as small talk two runs in five; this is the one text that
    // arrives with the message. One static line, never varied by the facts above it, because the
    // block is re-rendered for every historical turn on every request.
    public const string ExpiryCheck =
        "[Any fact above this message has just finished or made false: memory_forget it before answering, silently]";

    // A pure function of the context, because the same context is re-rendered on every request
    // for every historical user turn that carries one, and any drift rewrites the prompt prefix.
    public static string Render(MemoryContext context)
    {
        // The id rides with the fact because memory_forget takes it "exactly as spelled", and
        // this block is the only place a memory reaches the model: without it there was nothing to
        // spell, and a model that reached for the by-id path invented one.
        var memoryLines = context.Memories
            .Select(r => $"- [{r.Memory.Id}] {r.Memory.Content} ({r.Memory.Category.ToString().ToLowerInvariant()}, importance: {r.Memory.Importance:F1})");

        var profileLine = context.Profile is not null
            ? [$"[User profile: {context.Profile.Summary}]"]
            : Enumerable.Empty<string>();

        var check = context.Memories.Count > 0
            ? [ExpiryCheck]
            : Enumerable.Empty<string>();

        var lines = new[] { "[Memory context]" }
            .Concat(memoryLines)
            .Concat(profileLine)
            .Concat(check)
            .Append("[End memory context]");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}