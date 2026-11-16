using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebChat.Client.Components.Chat;

public sealed record OutlineNode(string? Label, string? Value, IReadOnlyList<OutlineNode> Children);

// An answer that is one JSON object, as an outline a person can read: each field under its key made
// words ("carbsPer100g" is "Carbs per 100g"), a list's entries as rows, and what is empty or null left
// out. Any structured format the chat has no view of its own for is drawn this way, so a new agent's
// JSON needs nothing written for it to be read.
public static partial class JsonOutline
{
    public static IReadOnlyList<OutlineNode>? Parse(string? content)
    {
        if (content is null || !content.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Fields(document.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<OutlineNode> Fields(JsonElement obj) =>
        [.. obj.EnumerateObject().Select(p => Node(Words(p.Name), p.Value)).OfType<OutlineNode>()];

    private static OutlineNode? Node(string? label, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object when Fields(value) is { Count: > 0 } fields => new OutlineNode(label, null, fields),
        JsonValueKind.Array when value.EnumerateArray().Select(e => Node(null, e)).OfType<OutlineNode>().ToList() is
        { Count: > 0 } entries => new OutlineNode(label, null, entries),
        JsonValueKind.String when value.GetString() is { Length: > 0 } text => new OutlineNode(label, text, []),
        JsonValueKind.Number => new OutlineNode(label, value.GetRawText(), []),
        JsonValueKind.True => new OutlineNode(label, "yes", []),
        JsonValueKind.False => new OutlineNode(label, "no", []),
        _ => null
    };

    private static string Words(string key)
    {
        var spaced = CamelBoundary().Replace(key.Replace('_', ' '), " ").Trim().ToLowerInvariant();
        return spaced.Length == 0 ? key : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    // A lowercase letter before an uppercase one, or a letter before a digit run: "carbsPer100g".
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d+)")]
    private static partial Regex CamelBoundary();
}