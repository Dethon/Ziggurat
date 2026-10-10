using System.Text.Json.Nodes;

namespace Domain.Tools;

// A footprint a tool server can declare over the wire, in the tool's own `_meta`: the resources
// a call reads and the ones it may change, each a path whose `{name}` segments are filled from
// the call's arguments.
//
//   "footprint": { "changes": ["browser/page/{url}"] }
//
// An argument is one segment however many slashes it holds, so a url names one page. A
// placeholder the call left out ends the path there — the claim widens to what was named, never
// narrows. `{}` is a tool that shares nothing; a shape this cannot read is no declaration at all,
// and the tool is ordered against everything like any other that said nothing.
public static class FootprintTemplate
{
    public const string MetaKey = "footprint";

    private const string Reads = "reads";
    private const string Changes = "changes";

    public static FootprintDeclaration? Parse(JsonNode? declared) =>
        declared is JsonObject shape
        && shape.All(field => field.Key is Reads or Changes)
        && Paths(shape[Reads]) is { } reads
        && Paths(shape[Changes]) is { } changes
            ? new FootprintDeclaration(call =>
                CallFootprint.Reading([.. reads.Select(path => Fill(path, call))])
                    .And(CallFootprint.Changing([.. changes.Select(path => Fill(path, call))])))
            : null;

    private static string[][]? Paths(JsonNode? declared) => declared switch
    {
        null => [],
        JsonArray paths when paths.All(path => Text(path) is not null) =>
            [.. paths.Select(path => Text(path)!.Split('/', StringSplitOptions.RemoveEmptyEntries))],
        _ => null
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static SharedResource Fill(string[] path, IReadOnlyDictionary<string, object?> call) =>
        SharedResource.Of([
            .. path
                .Select(segment => segment is ['{', .. var name, '}'] ? CallArguments.Text(call, name) : segment)
                .TakeWhile(segment => !string.IsNullOrWhiteSpace(segment))
                .Select(segment => segment!)
        ]);
}