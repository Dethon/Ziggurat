using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Editing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace McpServerSugary.App;

// A sheet open on a recipe, a food or a meal saves the whole record, and another person — or the same
// person's other phone — may have saved it since the sheet opened: Ana raised a recipe's flour, Dani's
// sheet changed its servings, and Ana's flour went back to what Dani had loaded. A save that says what
// it opened with has only what it changed laid over the record as it is now, field by field (as the
// admin pages are, by the user's choice), so the other change stands; a field both changed to
// different values is refused, nothing saved. A save naming nothing it loaded is saved as sent.
//
// All three are put in the server's own spelling before they are compared, so an instant or a number
// the app spelled its own way is not taken for a change.
public static class StaleEdits
{
    public sealed record Merged<T>(T? Value, IReadOnlyList<string> Conflicts);

    public static Merged<T> Merge<T>(
        HttpContext http, T loaded, T mine, T stored, IReadOnlyDictionary<string, string>? keyedBy = null)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var merged = EditMerge.Merge(
            Node(loaded, options), Node(mine, options), Node(stored, options),
            keyedBy ?? new Dictionary<string, string>());
        if (merged.Conflicts.Count > 0)
        {
            return new Merged<T>(default, [.. merged.Conflicts.Select(path => path.FirstOrDefault() ?? "").Distinct()]);
        }

        return new Merged<T>(merged.Value.Deserialize<T>(options), []);
    }

    public static IResult Conflict(IReadOnlyList<string> fields) =>
        Results.Problem(
            $"Changed elsewhere since it was opened: {string.Join(", ", fields)}. Open it again to see that change, then make yours.",
            statusCode: StatusCodes.Status409Conflict);

    private static JsonNode? Node<T>(T value, JsonSerializerOptions options) => JsonSerializer.SerializeToNode(value, options);
}