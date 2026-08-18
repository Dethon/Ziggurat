using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Utils;
using Tests.Eval.Fixtures;

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
        .. OverCeiling(scenario, recording),
        .. Answered(scenario, recording),
        .. Moved(scenario, recording),
        .. Wrote(scenario, recording),
        .. Delegated(scenario, recording)
    ];

    // Each declared task takes a delegation of its own, and every delegation has to answer to one:
    // two independent halves are two workers running at once, and one worker told to do both is
    // the sequence delegating was supposed to avoid — with a prompt that would satisfy both.
    private static IEnumerable<string> Delegated(Scenario scenario, Recording recording)
    {
        var unmatched = recording.Delegations.ToList();

        var missing = scenario.Delegates
            .Select(expectation =>
            {
                // A delegation to the right profile that is missing something is consumed by the
                // expectation it was trying to answer, so it is reported once — as the context it
                // left out, rather than a second time as work nobody declared.
                var match = unmatched.FirstOrDefault(d => Answers(expectation, d));
                var attempt = match ?? unmatched.FirstOrDefault(d => SameProfile(expectation, d));
                if (attempt is not null)
                {
                    unmatched.Remove(attempt);
                }

                return match is not null
                    ? null
                    : $"nothing was delegated to '{expectation.Profile}' carrying " +
                      $"{string.Join(", ", expectation.Carries.Select(c => $"'{c}'"))}. " +
                      $"Delegated: {Describe(recording)}";
            })
            .OfType<string>()
            .ToList();

        var undeclared = unmatched
            .Where(delegation => !scenario.MayDelegateTo.Contains(
                delegation.ProfileId, StringComparer.OrdinalIgnoreCase))
            .Select(delegation =>
            $"'{delegation.ProfileId}' was handed work the scenario did not declare: " +
            $"\"{delegation.Prompt}\"");

        return [.. missing, .. undeclared];
    }

    private static bool SameProfile(DelegationExpectation expectation, Delegation delegation) =>
        string.Equals(expectation.Profile, delegation.ProfileId, StringComparison.OrdinalIgnoreCase);

    private static bool Answers(DelegationExpectation expectation, Delegation delegation) =>
        SameProfile(expectation, delegation)
        && expectation.Carries.All(text =>
            delegation.Prompt.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static string Describe(Recording recording) =>
        recording.Delegations.Count == 0
            ? "nothing"
            : string.Join("; ", recording.Delegations.Select(d => $"{d.ProfileId}: \"{d.Prompt}\""));

    // What the files say once the turn is over, which is a different question from what was
    // written: an edit that lands the sentence the user asked for and turns a wikilink into
    // markdown on the way past made exactly one tool call, and it looked fine.
    private static IEnumerable<string> Wrote(Scenario scenario, Recording recording) =>
        scenario.Files.SelectMany(expectation =>
            recording.FilesAfter.TryGetValue(expectation.Path, out var content)
                ? Says(expectation, content, recording.FilesBefore.GetValueOrDefault(expectation.Path))
                : [$"{expectation.Path} does not exist after the turn"]);

    private static IEnumerable<string> Says(FileExpectation expectation, string content, string? before)
    {
        var missing = expectation.Contains
            .Where(text => !content.Contains(text, StringComparison.Ordinal))
            .Select(text => $"{expectation.Path} no longer carries '{text}'");

        var lingering = expectation.Absent
            .Where(text => content.Contains(text, StringComparison.Ordinal))
            .Select(text => $"{expectation.Path} still carries '{text}'");

        var rewritten = expectation.Unchanged && before is not null && before != content
            ? [$"{expectation.Path} was to be left unchanged and was rewritten"]
            : Enumerable.Empty<string>();

        return [.. missing, .. lingering, .. rewritten];
    }

    // Both halves of the same question, answered from one diff: did the change the user asked for
    // actually happen, and did anything else move while it did. The first catches a reply that
    // claims success over a call that failed; the second catches the cascade no permitted set can
    // see.
    private static IEnumerable<string> Moved(Scenario scenario, Recording recording)
    {
        var changed = recording.StateAfter
            .Where(entry => recording.StateBefore.GetValueOrDefault(entry.Key) != entry.Value)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        var undeclared = changed
            .Where(entry => !scenario.Changes.Any(c => c.Key == entry.Key))
            .Select(entry =>
                $"{entry.Key} changed to '{entry.Value}' and the scenario did not declare it " +
                $"(it was '{recording.StateBefore.GetValueOrDefault(entry.Key) ?? "absent"}')");

        var missing = scenario.Changes
            .Where(change => changed.GetValueOrDefault(change.Key) != change.To)
            .Select(change =>
                $"{change.Key} was to end at '{change.To}' and is at " +
                $"'{recording.StateAfter.GetValueOrDefault(change.Key) ?? "absent"}'");

        return [.. missing, .. undeclared];
    }

    private static IReadOnlyList<string> Answered(Scenario scenario, Recording recording) =>
        scenario.Reply is null ? [] : ReplyChecks.Failures(scenario.Reply, recording.Reply);

    private static IEnumerable<string> MissingRequired(Scenario scenario, Recording recording) =>
        scenario.Required
            .Where(expectation => Match(expectation, recording) is null)
            .Select(expectation =>
                $"required call '{expectation.Label}' never happened: expected {expectation.Tool} with " +
                $"{Describe(expectation)}. Seen: {Seen(recording)}");

    // Delegation is left out of this check on purpose: a delegated task is policed exhaustively by
    // the scenario's own declaration, which says which worker may be handed what — so a scenario
    // that also had to permit the tool by name would be saying the same thing twice, and a scenario
    // that forgot would report the model's correct decision as an unnecessary call.
    private static IEnumerable<string> Unnecessary(Scenario scenario, Recording recording)
    {
        // Compiled once for the whole recording rather than once per call: a permission is a pair
        // of wildcard patterns, and rebuilding both regexes per call is work nobody asked for.
        var permitted = scenario.Permitted
            .Select(p => (Tool: new ToolPatternMatcher([p.Tool]), Path: new ToolPatternMatcher([p.Path])))
            .ToList();

        return recording.Calls
            .Where(call => !string.Equals(call.ToolName, EvalTools.Delegate, StringComparison.Ordinal))
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