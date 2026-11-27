using System.Text.Encodings.Web;
using System.Text.Json;
using Domain.DTOs.WebChat;

namespace WebChat.Client.Components.Chat;

// How a tool call reads on the page: its name without the server's qualifier, the one argument that
// says what it did, its arguments laid out as fields, and its answer as what it is — a command's output,
// a failure's envelope, JSON or text. Everything here is text the page escapes: a call's arguments and
// answer are whatever a model wrote or a web page said, never markup.
public static class ToolDisplay
{
    public sealed record ToolName(string Bare, string? Server);

    public sealed record Field(string Key, string Value, bool Block);

    public abstract record ResultView;

    public sealed record CommandOutput(string? Stdout, string? Stderr, int? ExitCode, string? Detail) : ResultView;

    public sealed record Failure(string? Code, string Message, string? Hint) : ResultView;

    public sealed record Json(string Text) : ResultView;

    public sealed record Text(string Value) : ResultView;

    private static readonly JsonSerializerOptions _indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // The arguments that say most about what a call did, in the order they are looked for.
    private static readonly string[] _telling =
        ["command", "path", "query", "url", "pattern", "name", "prompt", "text", "message", "id"];

    private const int SummaryLength = 96;
    private const int InlineLength = 80;

    // mcp__timers__timer_set is timer_set, on timers; domain__filesystem__exec is exec, on filesystem.
    public static ToolName NameOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return new ToolName("tool", null);
        }

        var parts = name.Split("__", StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => new ToolName(name, null),
            1 => new ToolName(parts[0], null),
            _ => new ToolName(parts[^1], ServerLabel(parts[^2]))
        };
    }

    // A server's qualifier is its host, port and all (mcp-vault-8080): a person knows it as vault.
    private static string ServerLabel(string qualifier)
    {
        var label = qualifier.StartsWith("mcp-", StringComparison.Ordinal) ? qualifier[4..] : qualifier;
        var dash = label.LastIndexOf('-');
        return dash > 0 && label[(dash + 1)..].All(char.IsAsciiDigit) ? label[..dash] : label;
    }

    public static string Summary(string? arguments)
    {
        if (Parse(arguments) is not { ValueKind: JsonValueKind.Object } args)
        {
            return "";
        }

        var said = args.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 })
            .ToList();
        if (said.Count == 0)
        {
            return "";
        }

        var property = _telling.Select(key => said.FindIndex(p => p.Name == key)).FirstOrDefault(i => i >= 0, 0);
        var line = string.Join(' ', said[property].Value.GetString()!.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= SummaryLength ? line : line[..SummaryLength] + "…";
    }

    public static IReadOnlyList<Field> Fields(string? arguments) =>
        Parse(arguments) is { ValueKind: JsonValueKind.Object } args
            ? [.. args.EnumerateObject().Select(FieldOf)]
            : string.IsNullOrWhiteSpace(arguments) ? [] : [new Field("arguments", arguments, true)];

    public static ResultView? ResultOf(ToolCallStep step)
    {
        if (step.Result is not { } result)
        {
            return null;
        }

        if (Parse(result) is not { } json)
        {
            return new Text(result);
        }

        if (json.ValueKind == JsonValueKind.Object)
        {
            if (json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                return new Failure(
                    StringOf(json, "errorCode"),
                    StringOf(json, "message") ?? Pretty(json),
                    StringOf(json, "hint"));
            }

            if (json.TryGetProperty("stdout", out _) || json.TryGetProperty("stderr", out _))
            {
                return new CommandOutput(
                    StringOf(json, "stdout"),
                    StringOf(json, "stderr"),
                    json.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var exit) ? exit : null,
                    CommandDetail(json));
            }
        }

        return json.ValueKind == JsonValueKind.String ? new Text(json.GetString() ?? "") : new Json(Pretty(json));
    }

    // Whether a call ran a command, which reads as a terminal rather than a tool.
    public static bool RunsACommand(ToolCallStep step) =>
        Parse(step.Arguments) is { ValueKind: JsonValueKind.Object } args && args.TryGetProperty("command", out var command)
                                                                         && command.ValueKind == JsonValueKind.String;

    private static string? CommandDetail(JsonElement json)
    {
        var parts = new List<string>();
        if (json.TryGetProperty("durationMs", out var ms) && ms.TryGetInt64(out var duration))
        {
            parts.Add(duration < 1000 ? $"{duration} ms" : $"{duration / 1000.0:0.#} s");
        }

        if (json.TryGetProperty("timedOut", out var timedOut) && timedOut.ValueKind == JsonValueKind.True)
        {
            parts.Add("timed out");
        }

        if (json.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
        {
            parts.Add("output truncated");
        }

        if (StringOf(json, "cwd") is { Length: > 0 } cwd)
        {
            parts.Add(cwd);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static Field FieldOf(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.String)
        {
            var text = property.Value.GetString() ?? "";
            return new Field(property.Name, text, text.Contains('\n') || text.Length > InlineLength);
        }

        var value = property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? Pretty(property.Value)
            : property.Value.GetRawText();
        return new Field(property.Name, value, property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array);
    }

    private static string? StringOf(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Pretty(JsonElement json) => JsonSerializer.Serialize(json, _indented);

    private static JsonElement? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart() is not (['{', ..] or ['[', ..] or ['"', ..]))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}