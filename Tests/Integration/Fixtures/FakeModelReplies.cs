using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.Conversations;

namespace Tests.Integration.Fixtures;

// What the fake model says, as a pure function of the request it was sent: the suite's tests share
// one fake at once (the E2E stack's agent serves every test's turns), so an answer cannot come off a
// queue whose order belongs to whichever test asked first. The rules read the words the suite's tests
// already send — "reply with only the word X", "answer only OK", a demand to call the glob tool, a
// very long story — and anything else gets one short sentence.
public static partial class FakeModelReplies
{
    public const string ShortReply = "OK, noted.";

    // Long enough that a test has the time to stop it, short enough that one nothing stops ends.
    public const int StoryChunks = 300;
    public static readonly TimeSpan StoryChunkDelay = TimeSpan.FromMilliseconds(100);

    public abstract record Output;

    public sealed record Text(IReadOnlyList<string> Chunks, TimeSpan ChunkDelay) : Output
    {
        public string Whole => string.Concat(Chunks);
    }

    public sealed record ToolCall(string Name, string Arguments) : Output
    {
        public string Id { get; } = $"call_{Guid.NewGuid():N}";
    }

    // The reply as items in the order the model emits them: reasoning (when asked for), then text or
    // one tool call.
    // Its own ids, as a provider's are: the agent and the chat key a streamed message by its id, so two
    // replies in one conversation must never share one.
    public sealed record Reply(string? Reasoning, Output Output)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
    }

    public static Reply For(JsonObject request)
    {
        var reasoning = AsksForReasoning(request) ? "Thinking about it step by step." : null;
        return new Reply(reasoning, OutputFor(request));
    }

    private static Output OutputFor(JsonObject request)
    {
        if (request["text"]?["format"] is JsonObject { } format && format["type"]?.GetValue<string>() == "json_schema")
        {
            var schema = format["schema"] as JsonObject ?? new JsonObject();
            return Says(Instance(schema, schema)?.ToJsonString() ?? "{}");
        }

        var items = Items(request);
        var lastUser = items.FindLastIndex(item => Role(item) == "user");
        if (lastUser < 0)
        {
            return Says(ShortReply);
        }

        // A Continue carries on whatever the message before it asked for.
        var asking = items.Take(lastUser).LastOrDefault(item => Role(item) == "user");
        var instruction = TextOf(items[lastUser]) is var last && last.Contains(Continuation.Instruction, StringComparison.Ordinal)
                          && asking is not null
            ? TextOf(asking)
            : TextOf(items[lastUser]);
        var lastCall = items.FindLastIndex(item => Type(item) == "function_call");
        var lastSpoke = items.FindLastIndex(item => Role(item) == "assistant");

        // A tool called in this turn has answered: a message steered in after the call is what is
        // answered now, and otherwise whatever the demand said to say once the tool was done.
        if (lastCall > lastSpoke)
        {
            var steered = WordsIn(items.Skip(lastCall + 1));
            return Says(steered.Count > 0 ? string.Join(' ', steered)
                : lastUser > lastCall ? ShortReply
                : AfterTool(instruction));
        }

        if (DemandsGlob().IsMatch(instruction) && ToolNamed(request, "glob") is { } glob)
        {
            return new ToolCall(glob, """{"pattern":"**/*"}""");
        }

        if (instruction.Contains("very long and detailed story", StringComparison.OrdinalIgnoreCase))
        {
            return Story();
        }

        // Every user message since the model last spoke: steers sent together are answered together.
        var words = WordsIn(items.Skip(Math.Max(lastSpoke, lastCall) + 1));
        return Says(words.Count > 0 ? string.Join(' ', words) : ShortReply);
    }

    // The words asked for by the user messages among these, leaving out a tool demand an earlier,
    // turned-down turn left unanswered: its "reply with the word" was for after a tool that never ran.
    private static List<string> WordsIn(IEnumerable<JsonObject> items) =>
    [
        .. items.Where(item => Role(item) == "user")
            .Select(TextOf)
            .Where(text => !DemandsGlob().IsMatch(text))
            .SelectMany(text => WordAsked().Matches(text).Select(m => m.Groups["word"].Value))
    ];

    private static string AfterTool(string instruction) =>
        WordAsked().Match(instruction) is { Success: true } word ? word.Groups["word"].Value
        : SayQuoted().Match(instruction) is { Success: true } said ? said.Groups["word"].Value
        : "Done.";

    private static Text Says(string text) => new([text], TimeSpan.Zero);

    private static Text Story() => new(
        [.. Enumerable.Range(1, StoryChunks).Select(i => $"Chapter line {i} of the voyage went on. ")],
        StoryChunkDelay);

    private static bool AsksForReasoning(JsonObject request) =>
        request["reasoning"] is JsonObject reasoning
        && reasoning["effort"]?.GetValue<string>() is { } effort
        && effort != "none"
        && reasoning["summary"] is not null;

    private static List<JsonObject> Items(JsonObject request) => request["input"] switch
    {
        JsonArray input => [.. input.OfType<JsonObject>()],
        JsonValue text => [new JsonObject { ["role"] = "user", ["content"] = text.GetValue<string>() }],
        _ => []
    };

    private static string? Role(JsonObject item) => item["role"]?.GetValue<string>();

    private static string? Type(JsonObject item) => item["type"]?.GetValue<string>();

    private static string TextOf(JsonObject item) => item["content"] switch
    {
        JsonValue text => text.GetValue<string>(),
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
            .Select(part => part["text"]?.GetValue<string>())
            .OfType<string>()),
        _ => ""
    };

    // By the tool's own name or the last segment of a namespaced one, so the domain tool and the raw
    // MCP one both answer to "glob".
    public static string? ToolNamed(JsonObject request, string name) =>
        (request["tools"] as JsonArray)?.OfType<JsonObject>()
        .Select(tool => tool["name"]?.GetValue<string>() ?? tool["function"]?["name"]?.GetValue<string>())
        .OfType<string>()
        .FirstOrDefault(tool => tool == name || tool.EndsWith($"_{name}", StringComparison.Ordinal)
                                || tool.EndsWith($"__{name}", StringComparison.Ordinal));

    // A minimal instance of a strict schema: every declared property, the first enum value, one item
    // per array, and numbers that are positive, so an estimate reads as some food rather than none.
    private static JsonNode? Instance(JsonObject schema, JsonObject root)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            var target = reference.TrimStart('#', '/').Split('/')
                .Aggregate((JsonNode?)root, (node, segment) => node?[segment]);
            return target is JsonObject resolved ? Instance(resolved, root) : null;
        }

        if (schema["const"] is { } constant)
        {
            return constant.DeepClone();
        }

        if (schema["enum"] is JsonArray values)
        {
            return values.FirstOrDefault(v => v is not null)?.DeepClone();
        }

        if ((schema["anyOf"] ?? schema["oneOf"]) is JsonArray choices)
        {
            var chosen = choices.OfType<JsonObject>().FirstOrDefault(c => TypeOf(c) != "null");
            return chosen is null ? null : Instance(chosen, root);
        }

        return TypeOf(schema) switch
        {
            "object" => new JsonObject(((schema["properties"] as JsonObject) ?? [])
                .Select(p => KeyValuePair.Create(p.Key, p.Value is JsonObject property ? Instance(property, root) : null))),
            "array" => schema["items"] is JsonObject item ? new JsonArray(Instance(item, root)) : new JsonArray(),
            "integer" or "number" => JsonValue.Create(10),
            "boolean" => JsonValue.Create(false),
            "string" => JsonValue.Create("fake"),
            _ => null
        };
    }

    private static string? TypeOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue single => single.GetValue<string>(),
        JsonArray several => several.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t != "null"),
        _ => schema["properties"] is not null ? "object" : null
    };

    // "Reply with only the word X", "reply with the word X", "after it, the word X", "answer only X".
    [GeneratedRegex(@"(?:\bthe word|\banswer only)\s+(?<word>[A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WordAsked();

    [GeneratedRegex(@"\bsay\s+'(?<word>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex SayQuoted();

    [GeneratedRegex(@"MUST call a tool.*glob", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DemandsGlob();
}