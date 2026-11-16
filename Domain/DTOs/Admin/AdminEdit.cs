using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.DTOs.Admin;

// A section an admin page saves whole, sent with what the page loaded beside it (under "loaded"), so
// the admin API lays only what this page changed over the section as it is now: two owners with the
// same page open keep each other's changes, and a field both changed is refused rather than lost. A
// body with nothing loaded beside it (the operator's tools, a client from before) is saved as sent.
public static class AdminEdit
{
    public const string Loaded = "loaded";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static JsonObject Of<T>(T value, T? loaded) where T : class
    {
        var body = JsonSerializer.SerializeToNode(value, _json)!.AsObject();
        if (loaded is not null)
        {
            body[Loaded] = JsonSerializer.SerializeToNode(loaded, _json);
        }

        return body;
    }
}