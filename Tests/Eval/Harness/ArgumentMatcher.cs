using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tests.Eval.Harness;

// A question asked of one call's arguments, carrying the words its failure will be reported in.
// The predicate reads the whole argument object rather than one value, because half of what these
// scenarios discriminate on sits inside a JSON document written into a single string argument.
public sealed record ArgumentMatcher(string Description, Func<JsonElement, bool> Matches);

// The argument matchers a scenario is written with. Timers, schedules and Home Assistant are all
// reached through the same handful of filesystem tools, so "which tool was selected" is mostly
// "which path was written to" — these carry nearly all of the signal in the suite.
public static class Arg
{
    public static ArgumentMatcher Is(string name, string value) =>
        new($"{name} = '{value}'",
            args => Read(args, name) is { ValueKind: JsonValueKind.String } element
                    && element.GetString() == value);

    public static ArgumentMatcher Matches(string name, string pattern) =>
        new($"{name} matches /{pattern}/",
            args => Read(args, name) is { ValueKind: JsonValueKind.String } element
                    && Regex.IsMatch(element.GetString() ?? "", pattern, RegexOptions.IgnoreCase));

    // An argument whose value is itself a document: the body of a timer, a schedule or a note.
    // Either spelling counts — a model writes the file body as a JSON string and its nested keys
    // as real objects, and the scenario is asking about the values rather than about which of the
    // two the model chose.
    public static ArgumentMatcher Body(string name, params ArgumentMatcher[] inner) =>
        new($"{name} = {{{string.Join(", ", inner.Select(m => m.Description))}}}",
            args => Read(args, name) switch
            {
                { ValueKind: JsonValueKind.Object } nested => inner.All(m => m.Matches(nested)),
                { ValueKind: JsonValueKind.String } text => Parsed(text.GetString(), inner),
                _ => false
            });

    private static bool Parsed(string? text, IReadOnlyList<ArgumentMatcher> inner)
    {
        try
        {
            using var body = JsonDocument.Parse(text ?? "");
            return inner.All(m => m.Matches(body.RootElement));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The path, whatever this tool calls it. Create and read say `filePath`, remove and info say
    // `path`, glob says `basePath` — and what a scenario is asking about is the same question in
    // all three cases, because for most of this suite "which tool was selected" is "which path was
    // written to".
    public static ArgumentMatcher Path(string value) =>
        new($"path = '{value}'", args => PathOf(args) == value);

    public static ArgumentMatcher PathMatches(string pattern) =>
        new($"path matches /{pattern}/",
            args => Regex.IsMatch(PathOf(args) ?? "", pattern, RegexOptions.IgnoreCase));

    public static readonly string[] PathNames = ["path", "filePath", "basePath", "sourcePath"];

    public static string? PathOf(JsonElement args) =>
        PathNames
            .Select(name => Read(args, name))
            .FirstOrDefault(element => element is { ValueKind: JsonValueKind.String })
            ?.GetString();

    public static ArgumentMatcher Number(string name, double value) =>
        new($"{name} = {value}",
            args => Read(args, name) is { ValueKind: JsonValueKind.Number } element
                    && Math.Abs(element.GetDouble() - value) < 0.0001);

    // Case-insensitive by name: a model writes `durationSeconds` where the schema says so, but the
    // question a scenario is asking is about the value rather than the spelling of the key.
    private static JsonElement? Read(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
            ? args.EnumerateObject()
                .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(p => (JsonElement?)p.Value)
                .FirstOrDefault()
            : null;
}