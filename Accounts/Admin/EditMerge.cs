using System.Text.Json.Nodes;

namespace Accounts.Admin;

// A section merged, and the fields both sides changed to different values (each a path: property
// names, and an array element by its key), in which case the value is not to be saved.
public sealed record MergedEdit(JsonNode? Value, IReadOnlyList<IReadOnlyList<string>> Conflicts);

// Two owners with the same admin page open: each page sends what it loaded beside what it wants, and
// what it changed is laid over the section as it is now, field by field, so another owner's change to
// anything else stands. An array of objects is merged element by element where its property names the
// element's key (a space by its slug); any other value is one field. A field changed on both sides to
// different values is a conflict, as is an element removed on one side and changed on the other.
public static class EditMerge
{
    public static MergedEdit Merge(
        JsonNode? loaded, JsonNode? mine, JsonNode? stored, IReadOnlyDictionary<string, string> arrayKeys)
    {
        var conflicts = new List<IReadOnlyList<string>>();
        var value = Node(loaded, mine, stored, [], null, arrayKeys, conflicts);
        return new MergedEdit(value, conflicts);
    }

    private static JsonNode? Node(
        JsonNode? loaded, JsonNode? mine, JsonNode? stored, IReadOnlyList<string> path, string? property,
        IReadOnlyDictionary<string, string> arrayKeys, List<IReadOnlyList<string>> conflicts)
    {
        if (Same(mine, loaded))
        {
            return stored?.DeepClone();
        }

        if (Same(stored, loaded) || Same(mine, stored))
        {
            return mine?.DeepClone();
        }

        switch (loaded, mine, stored)
        {
            case (JsonObject l, JsonObject m, JsonObject s):
                return Object(l, m, s, path, arrayKeys, conflicts);
            case (JsonArray l, JsonArray m, JsonArray s)
                when property is not null && arrayKeys.TryGetValue(property, out var key):
                return Keyed(l, m, s, key, path, arrayKeys, conflicts);
            default:
                conflicts.Add(path);
                return stored?.DeepClone();
        }
    }

    private static JsonObject Object(
        JsonObject loaded, JsonObject mine, JsonObject stored, IReadOnlyList<string> path,
        IReadOnlyDictionary<string, string> arrayKeys, List<IReadOnlyList<string>> conflicts)
    {
        var names = stored.Select(p => p.Key)
            .Concat(mine.Select(p => p.Key))
            .Concat(loaded.Select(p => p.Key))
            .Distinct(StringComparer.Ordinal);
        return new JsonObject(names
            .Select(name => (Name: name,
                Value: Node(Get(loaded, name), Get(mine, name), Get(stored, name), [.. path, name], name, arrayKeys, conflicts)))
            .Where(field => field.Value is not null || stored.ContainsKey(field.Name) || mine.ContainsKey(field.Name))
            .Select(field => KeyValuePair.Create(field.Name, field.Value)));
    }

    private static JsonArray Keyed(
        JsonArray loaded, JsonArray mine, JsonArray stored, string key, IReadOnlyList<string> path,
        IReadOnlyDictionary<string, string> arrayKeys, List<IReadOnlyList<string>> conflicts)
    {
        var l = ByKey(loaded, key);
        var m = ByKey(mine, key);
        var s = ByKey(stored, key);
        var keys = s.Keys.Concat(m.Keys).Concat(l.Keys).Distinct(StringComparer.Ordinal);
        return
        [
            .. keys
                .Select(k => Node(l.GetValueOrDefault(k), m.GetValueOrDefault(k), s.GetValueOrDefault(k), [.. path, k], null,
                    arrayKeys, conflicts))
                .OfType<JsonNode>()
        ];
    }

    private static Dictionary<string, JsonNode> ByKey(JsonArray array, string key) =>
        array.OfType<JsonObject>()
            .Where(element => element[key] is JsonValue)
            .GroupBy(element => element[key]!.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (JsonNode)group.First(), StringComparer.Ordinal);

    // A null value and an absent property are one: a section's shape has no null it tells apart.
    private static JsonNode? Get(JsonObject owner, string name) => owner.TryGetPropertyValue(name, out var value) ? value : null;

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
}