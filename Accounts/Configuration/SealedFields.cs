using System.Text;
using System.Text.Json.Nodes;
using Domain.Security.Sealing;

namespace Accounts.Configuration;

// The secrets inside a section's document. At rest a secret is the object {"sealed": "<base64 blob>"}
// where its value was, sealed for its own path (HouseholdSealing), so a stored document says for
// itself which fields are sealed whatever shape it was written in. To an operator it is
// {"sealed": true}: printed for every secret instead of its value, and read back on a save as "keep
// what is stored here", so a section read, edited and saved again keeps keys nobody retyped.
public static class SealedFields
{
    private const string Property = "sealed";

    public static bool AnySealed(JsonNode? node) => Count(node) > 0;

    public static int Count(JsonNode? node) => SealedIn(node, "").Count();

    // Whether an operator's document asks to keep any stored secret (or carries a sealed value).
    public static bool HasMarkers(JsonNode? node) => Markers(node, "").Any();

    // The stored document with every sealed field opened in place.
    public static JsonObject Open(JsonObject stored, SealingKey? dataKey, string householdId, string section)
    {
        var copy = stored.DeepClone().AsObject();
        foreach (var (path, holder, sealedValue) in SealedIn(copy, "").ToList())
        {
            if (dataKey is null)
            {
                throw new HouseholdSecretsUnavailableException(
                    $"household {householdId}'s {section} section holds a sealed field and the household has no data key");
            }

            var opened = dataKey.Open(Convert.FromBase64String(sealedValue), HouseholdSealing.AssociatedData(householdId, section, path));
            if (!opened.IsOpened)
            {
                throw new HouseholdSecretsUnavailableException(
                    $"household {householdId}'s {section} field {path} does not open ({opened.Refusal})");
            }

            holder.ReplaceWith(Encoding.UTF8.GetString(opened.Value));
        }

        return copy;
    }

    // The plain document with every secret the schema names sealed for its path. A secret left out
    // or null stays so.
    public static JsonObject Seal(JsonObject plain, IReadOnlyList<string> secretPaths, SealingKey dataKey,
        string householdId, string section)
    {
        var copy = plain.DeepClone().AsObject();
        foreach (var (path, value) in Secrets(copy, secretPaths).Where(s => s.Value is JsonValue).ToList())
        {
            var blob = dataKey.Seal(Encoding.UTF8.GetBytes(value!.GetValue<string>()),
                HouseholdSealing.AssociatedData(householdId, section, path));
            value.ReplaceWith(new JsonObject { [Property] = Convert.ToBase64String(blob) });
        }

        return copy;
    }

    public static bool HasSecret(JsonObject plain, IReadOnlyList<string> secretPaths) =>
        Secrets(plain, secretPaths).Any(s => s.Value is JsonValue);

    // The plain document as an operator may see it: every secret replaced by the keep marker.
    public static JsonObject Mask(JsonObject plain, IReadOnlyList<string> secretPaths)
    {
        var copy = plain.DeepClone().AsObject();
        foreach (var (_, value) in Secrets(copy, secretPaths).Where(s => s.Value is JsonValue).ToList())
        {
            value!.ReplaceWith(new JsonObject { [Property] = true });
        }

        return copy;
    }

    // An operator's document with each keep marker replaced by the value stored at the same path, or
    // what could not be kept. A blob is never taken from a file: it was sealed for wherever it came
    // from, and a secret is set by writing it in clear.
    public static (JsonObject Document, IReadOnlyList<string> Problems) Keep(JsonObject input, JsonObject? stored)
    {
        var copy = input.DeepClone().AsObject();
        var problems = new List<string>();
        foreach (var (path, marker) in Markers(copy, "").ToList())
        {
            if (marker[Property] is not JsonValue flag || !flag.TryGetValue<bool>(out var keep) || !keep)
            {
                problems.Add($"{path} is a sealed value, which cannot be set: write the secret in clear, "
                             + $"or {{\"{Property}\": true}} to keep the one stored");
                continue;
            }

            if (At(stored, path) is JsonValue kept)
            {
                marker.ReplaceWith(kept.DeepClone());
            }
            else
            {
                problems.Add($"{path} asks to keep the stored secret, and none is stored there");
            }
        }

        return (copy, problems);
    }

    private static bool IsSealed(JsonObject node, out string value)
    {
        value = "";
        return node.Count == 1 && node[Property] is JsonValue v && v.TryGetValue(out value!);
    }

    private static IEnumerable<(string Path, JsonObject Holder, string Value)> SealedIn(JsonNode? node, string path) => node switch
    {
        JsonObject o when IsSealed(o, out var value) => [(path, o, value)],
        JsonObject o => o.ToList().SelectMany(p => SealedIn(p.Value, Join(path, p.Key))),
        JsonArray a => a.Select((item, i) => (item, i)).ToList().SelectMany(e => SealedIn(e.item, Join(path, e.i.ToString()))),
        _ => []
    };

    private static IEnumerable<(string Path, JsonObject Marker)> Markers(JsonNode? node, string path) => node switch
    {
        JsonObject o when o.Count == 1 && o.ContainsKey(Property) => [(path, o)],
        JsonObject o => o.ToList().SelectMany(p => Markers(p.Value, Join(path, p.Key))),
        JsonArray a => a.Select((item, i) => (item, i)).ToList().SelectMany(e => Markers(e.item, Join(path, e.i.ToString()))),
        _ => []
    };

    // Every concrete field the schema's secret paths reach in this document, with its value.
    private static IEnumerable<(string Path, JsonNode? Value)> Secrets(JsonObject document, IReadOnlyList<string> patterns) =>
        patterns.SelectMany(pattern => Reach(document, pattern.Split('/'), ""));

    private static IEnumerable<(string Path, JsonNode? Value)> Reach(JsonNode? node, ReadOnlyMemory<string> segments, string path)
    {
        if (segments.IsEmpty)
        {
            return node is null ? [] : [(path, node)];
        }

        if (node is not JsonObject o)
        {
            return [];
        }

        var segment = segments.Span[0];
        var rest = segments[1..];
        return segment == "*"
            ? o.ToList().SelectMany(p => Reach(p.Value, rest, Join(path, p.Key)))
            : o.TryGetPropertyValue(segment, out var child) ? Reach(child, rest, Join(path, segment)) : [];
    }

    private static JsonNode? At(JsonNode? node, string path) =>
        path.Split('/').Select(Unescape).Aggregate(node, (current, segment) => current switch
        {
            JsonObject o => o[segment],
            JsonArray a when int.TryParse(segment, out var i) && i < a.Count => a[i],
            _ => null
        });

    // A path's segments are property names, escaped as a JSON pointer's are so a name holding `/`
    // cannot pass for two.
    private static string Join(string path, string segment)
    {
        var escaped = segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        return path.Length == 0 ? escaped : $"{path}/{escaped}";
    }

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}