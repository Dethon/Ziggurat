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

    public static ArgumentMatcher Absent(string name) =>
        new($"{name} absent", args => Read(args, name) is null);

    // An argument whose value is itself a JSON document, which is how every file this agent writes
    // reaches a tool: the body of a timer, a schedule or a note is one string argument.
    public static ArgumentMatcher Body(string name, params ArgumentMatcher[] inner) =>
        new($"{name} = {{{string.Join(", ", inner.Select(m => m.Description))}}}",
            args =>
            {
                if (Read(args, name) is not { ValueKind: JsonValueKind.String } element)
                {
                    return false;
                }

                try
                {
                    using var body = JsonDocument.Parse(element.GetString() ?? "");
                    return inner.All(m => m.Matches(body.RootElement));
                }
                catch (JsonException)
                {
                    return false;
                }
            });

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