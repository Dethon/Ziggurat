using System.Text.Json.Nodes;

namespace Tests.Integration.Fixtures;

// A Sugary sheet open on a record since before: what it opened with is the record as the server
// answered it, and every change it saves carries that beside it as `loaded` (McpServerSugary's
// StaleEdits).
public static class StaleSheet
{
    public static JsonObject Opened(JsonNode saved, params string[] fields) =>
        new(fields.Select(field => KeyValuePair.Create(field, saved[field]?.DeepClone())));

    public static JsonObject Changed(JsonObject opened, string field, JsonNode? value)
    {
        var change = (JsonObject)opened.DeepClone();
        change[field] = value;
        change["loaded"] = opened.DeepClone();
        return change;
    }
}