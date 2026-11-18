using System.Text.Json;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Skills;

// A preload as a model is sent it: one user-role message ahead of the request, a part per skill
// and per read, in place of the pair the thread records (`SkillLoadTool.AsPreloaded`). Written
// onto the copy that goes to the wire, the way a turn's decoration is, and never stored — so
// nothing that reads a thread for what a person said can find a skill body in their voice, and a
// pair stored by any earlier build is sent as the same note with nothing migrated.
//
// Why not the pair itself, which is what was sent until 2026-10-10: it is an assistant turn the
// model never took, and what a model makes of one depends on the model and on whoever serves it.
// OpenRouter drops reasoning it did not issue for Anthropic's models, so they saw a call of
// "theirs" with no thought and no opening word and carried on mid-turn — Haiku 5.5 opened slow
// spoken work with its one word on 0 runs of 10, gpt-6-luna on 4, and both on 10 of 10 with this
// note, as did DeepSeek, GLM and Mistral; DeepSeek's own host refuses a call without reasoning
// (HTTP 400), which the host then had to fabricate; Mistral reads nine characters of a call id.
// A user-role message is the one item every provider passes through where it was put. A
// `developer` or `system` item is not: OpenRouter hoists it to the head of the conversation for
// the hosts it reaches over chat completions, which moves the cached prefix.
//
// Ahead of the request since 2026-11-18, after it before: the note is the same bytes on every turn
// that preloads the same skill (the home skill and its setup index, ~9.4k tokens) and the request
// never is, so after it the note was never in the provider's cached prefix — a voice wake's first
// call sent it uncached every time. The pair placed before the request had cost the first action
// (Haiku named the right entity on 4 of 10 home turns against 10); the note in that place held on
// the eval's home and voice families: DeepSeek 98 of 103 runs against 99, and 39 of 48 against 41 on
// the one-word scenarios alone; Haiku 101 of 103 against 97. The message a turn is keyed
// on is read off the record, not the wire, so the note in front of it changes nothing there.
//
// A separate message rather than more parts on the person's own: OpenRouter merges the two for
// the hosts that want one user turn and leaves them apart for the rest, and where they stay
// apart the apart shape read better (a quantised GLM host named the right entity about half the
// time against a quarter).
public static class SkillPreloadNote
{
    // Measured as written, on five models: reworded, it is a different measurement. "Do not load
    // it again" holds for the life of the conversation; the read is dated to its request because
    // a file can change and the body says when to read it again.
    public static string Loaded(string skillName) =>
        $"[Loaded for this request before your first call, by the host: the `{skillName}` skill. Do not load it again.]";

    public static string Read(string path) =>
        $"[Read for this request before your first call, by the host: `{path}`. This is what `file_read` returned.]";

    // Every host-made pair in the conversation becomes its note, where it stood; everything else
    // is passed through as the instance it was. A call whose result is no longer here names
    // nothing the model can be shown, and goes unsent with whatever is left of its pair.
    public static IReadOnlyList<ChatMessage> AsSent(IReadOnlyList<ChatMessage> messages)
    {
        var hostCalls = messages
            .Where(IsHostCallMessage)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToHashSet(StringComparer.Ordinal);
        if (hostCalls.Count == 0)
        {
            return messages;
        }

        var results = messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Where(r => hostCalls.Contains(r.CallId))
            .GroupBy(r => r.CallId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Result, StringComparer.Ordinal);

        return messages
            .SelectMany(m => Sent(m, hostCalls, results))
            .Aggregate(new List<ChatMessage>(), Place);
    }

    // A note goes ahead of the request it was made for — the run of user messages it followed — so
    // what is the same on every turn that preloads the same skill sits before what never is, and
    // the provider's cached prefix reaches through it.
    private static List<ChatMessage> Place(List<ChatMessage> sent, (ChatMessage Message, bool IsNote) next)
    {
        if (!next.IsNote)
        {
            sent.Add(next.Message);
            return sent;
        }

        var request = sent.FindLastIndex(m => m.Role != ChatRole.User) + 1;
        sent.Insert(request, next.Message);
        return sent;
    }

    private static IEnumerable<(ChatMessage Message, bool IsNote)> Sent(
        ChatMessage message, IReadOnlySet<string> hostCalls, IReadOnlyDictionary<string, object?> results)
    {
        if (IsHostCallMessage(message))
        {
            var parts = message.Contents
                .OfType<FunctionCallContent>()
                .Select(call => results.TryGetValue(call.CallId, out var result) ? Part(call, result) : null)
                .OfType<AIContent>()
                .ToList();
            return parts.Count > 0 ? [(new ChatMessage(ChatRole.User, parts), true)] : [];
        }

        if (!message.Contents.OfType<FunctionResultContent>().Any(r => hostCalls.Contains(r.CallId)))
        {
            return [(message, false)];
        }

        var kept = message.Contents
            .Where(c => c is not FunctionResultContent r || !hostCalls.Contains(r.CallId))
            .ToList();
        if (kept.Count == 0)
        {
            return [];
        }

        var rest = message.Clone();
        rest.Contents = kept;
        return [(rest, false)];
    }

    // The host's calls stand alone on their message; a message that mixes one with a call a
    // model made is nothing the host wrote, and is sent as it is.
    private static bool IsHostCallMessage(ChatMessage message)
    {
        var calls = message.Contents.OfType<FunctionCallContent>().ToList();
        return message.Role == ChatRole.Assistant
            && calls.Count > 0
            && calls.All(c => SkillLoadTool.IsHostCall(c.CallId));
    }

    private static TextContent? Part(FunctionCallContent call, object? result)
    {
        var head = call.Name switch
        {
            SkillLoadTool.Name when Argument(call, SkillLoadTool.SkillNameParameter) is { } skill => Loaded(skill),
            _ when call.Name == SkillLoadTool.ReadToolName
                && Argument(call, VfsFileReadTool.FilePathParameter) is { } path => Read(path),
            _ => null
        };
        return head is null ? null : new TextContent($"{head}\n\n{Text(result)}");
    }

    private static string? Argument(FunctionCallContent call, string name) =>
        call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) ? value?.ToString() : null;

    // What the tool's result would have been on the wire: a string as it is, anything else as
    // the JSON the adapter writes for a result — indented, nothing escaped that a reader would
    // have to undo. One serializer for the object the turn holds and for the element the store
    // hands back, because the note has to be the same bytes on every later turn.
    private static string Text(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions)
    };
}