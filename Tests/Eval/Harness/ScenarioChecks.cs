using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Utils;

namespace Tests.Eval.Harness;

// Everything a scenario declares, decided against one recording. Nothing here reaches into the
// agent, the prompt assembly or a server's storage: what a scenario checks must not depend on how
// the agent is built, or a refactor becomes a behavioural failure.
public static class ScenarioChecks
{
    public static IReadOnlyList<string> Failures(Scenario scenario, Recording recording) =>
    [
        .. MissingRequired(scenario, recording),
        .. Unnecessary(scenario, recording),
        .. OutOfOrder(scenario, recording),
        .. OverCeiling(scenario, recording)
    ];

    private static IEnumerable<string> MissingRequired(Scenario scenario, Recording recording) =>
        scenario.Required
            .Where(expectation => Match(expectation, recording) is null)
            .Select(expectation =>
                $"required call '{expectation.Label}' never happened: expected {expectation.Tool} with " +
                $"{Describe(expectation)}. Seen: {Seen(recording)}");

    private static IEnumerable<string> Unnecessary(Scenario scenario, Recording recording)
    {
        // Compiled once for the whole recording rather than once per call: a permission is a pair
        // of wildcard patterns, and rebuilding both regexes per call is work nobody asked for.
        var permitted = scenario.Permitted
            .Select(p => (Tool: new ToolPatternMatcher([p.Tool]), Path: new ToolPatternMatcher([p.Path])))
            .ToList();

        return recording.Calls
            .Where(call => !scenario.Required.Any(e => Matches(e, call))
                           && !permitted.Any(p => p.Tool.IsMatch(call.ToolName) && p.Path.IsMatch(Path(call))))
            .Select(call =>
                $"unnecessary call: {call.ToolName} {call.Arguments} is neither required nor permitted");
    }

    // Pairwise and partial: the constraint is that one call precedes another, not that the
    // recording has a total order. Anything between them is somebody else's business.
    //
    // Satisfied by the earliest A against the latest B, because either side can happen more than
    // once: a model that deleted, thought better of it, read the status and deleted again did do
    // the two in the order the contract asks for, and matching only the first of each would call
    // that a violation on the strength of the false start.
    private static IEnumerable<string> OutOfOrder(Scenario scenario, Recording recording) =>
        scenario.Ordering.Select(constraint =>
        {
            var before = Matches(scenario, constraint.Before, recording).FirstOrDefault();
            var after = Matches(scenario, constraint.After, recording).LastOrDefault();

            return (before, after) switch
            {
                (null, _) => $"ordering '{constraint.Before}' before '{constraint.After}': " +
                             $"'{constraint.Before}' never happened",
                (_, null) => $"ordering '{constraint.Before}' before '{constraint.After}': " +
                             $"'{constraint.After}' never happened",
                var (b, a) when b.Sequence > a.Sequence =>
                    $"ordering '{constraint.Before}' before '{constraint.After}': out of order, " +
                    $"{a.ToolName} {a.Arguments} came first",
                _ => null
            };
        }).OfType<string>();

    private static IEnumerable<ToolInvocation> Matches(
        Scenario scenario, string label, Recording recording) =>
        recording.Calls.Where(call => Matches(Expectation(scenario, label), call));

    private static IEnumerable<string> OverCeiling(Scenario scenario, Recording recording) =>
        recording.Calls.Count > scenario.CallCeiling
            ? [$"call ceiling exceeded: {recording.Calls.Count} calls against a ceiling of " +
               $"{scenario.CallCeiling}. Seen: {Seen(recording)}"]
            : [];

    private static CallExpectation Expectation(Scenario scenario, string label) =>
        scenario.Required.FirstOrDefault(e => e.Label == label)
        ?? throw new InvalidOperationException(
            $"Scenario '{scenario.Name}' orders '{label}', which is not one of its required calls.");

    private static ToolInvocation? Match(CallExpectation expectation, Recording recording) =>
        recording.Calls.FirstOrDefault(call => Matches(expectation, call));

    private static bool Matches(CallExpectation expectation, ToolInvocation call)
    {
        if (!string.Equals(expectation.Tool, call.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            return expectation.Arguments.All(matcher => matcher.Matches(arguments.RootElement));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Path(ToolInvocation call)
    {
        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            return Arg.PathOf(arguments.RootElement) ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Describe(CallExpectation expectation) =>
        expectation.Arguments.Count == 0
            ? "any arguments"
            : string.Join(", ", expectation.Arguments.Select(m => m.Description));

    private static string Seen(Recording recording) =>
        recording.Calls.Count == 0
            ? "no calls at all"
            : string.Join("; ", recording.Calls.Select(c => $"{c.ToolName} {c.Arguments}"));
}