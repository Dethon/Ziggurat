using System.Text.Json.Nodes;

namespace Mcp.Hosting;

// A string a call carries under a key — in its params or its `_meta` — whatever shape the caller
// sent: anything that is not a string there is none.
internal static class CallText
{
    public static string? StringAt(JsonObject? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}