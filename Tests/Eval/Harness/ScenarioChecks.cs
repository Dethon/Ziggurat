using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.Tools.Web;
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
        .. TimedOut(recording),
        .. OverCeiling(scenario, recording),
        .. Answered(scenario, recording),
        .. Moved(scenario, recording),
        .. Wrote(scenario, recording),
        .. Delegated(scenario, recording),
        .. ConditionallyDelegated(scenario, recording),
        .. Forgot(scenario, recording)
    ];

    // Which red this is. A turn the provider refused or that ran out of time is neither prose's
    // fault: the model never answered, so its silence says nothing about a description. Otherwise
    // a required load that did not happen — or happened for another skill, which is the same
    // absence — is the description's failure; anything else, the load included and a behaviour
    // missing, is the body's. Null for a run with nothing to explain.
    public static FailureKind? KindOf(Scenario scenario, Recording recording, IReadOnlyList<string> failures) =>
        failures.Count == 0
            ? null
            : recording.ProviderError is not null || recording.TimedOut
                ? FailureKind.RunFailed
                : scenario.Required
                    .Where(expectation => new ToolPatternMatcher([expectation.Tool]).IsMatch(EvalTools.LoadSkill))
                    .Any(expectation => Match(expectation, recording) is null)
                    ? FailureKind.SkillNotLoaded
                    : FailureKind.RuleIgnored;

    // The conditional half of Delegated: a run that handed nothing to the profile owes nothing
    // here, but every delegation it did receive must carry the condition's context — a split
    // into two workers is legitimate, and each starts with no history.
    private static IEnumerable<string> ConditionallyDelegated(Scenario scenario, Recording recording) =>
        scenario.IfDelegated.SelectMany(condition =>
            recording.Delegations
                .Where(delegation => Answers(condition, delegation))
                .SelectMany(delegation => condition.Carries
                    .Where(text => !delegation.Prompt.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Select(text =>
                        $"'{delegation.ProfileId}' was handed a prompt without '{text}': " +
                        $"\"{delegation.Prompt}\"")));

    private static bool Answers(ConditionalDelegation condition, Delegation delegation) =>
        string.Equals(condition.Profile, delegation.ProfileId, StringComparison.OrdinalIgnoreCase);

    // The judged checks this run owes: the scenario's own, plus the conditional ones whose
    // delegated prompt exists to be graded. A judge paid to fail on material a legitimate
    // in-place run never produced would turn the model's own coin into a red run.
    public static IReadOnlyList<JudgedCheck> JudgedNow(Scenario scenario, Recording recording) =>
    [
        .. scenario.Judged,
        .. Triggered(scenario, recording).SelectMany(condition => condition.Judged)
    ];

    // The conditional claims this run produced material for, deterministic and judged alike:
    // what the runner tallies them over, so their scorecard denominator is runs with a
    // delegation rather than runs taken.
    public static IReadOnlyList<string> Exercised(Scenario scenario, Recording recording) =>
        [.. Triggered(scenario, recording)
            .SelectMany(condition => condition.Claims.Concat(condition.Judged.Select(j => j.Claim)))
            .Distinct()];

    private static IEnumerable<ConditionalDelegation> Triggered(Scenario scenario, Recording recording) =>
        scenario.IfDelegated
            .Where(condition => recording.Delegations.Any(d => Answers(condition, d)));

    // Both directions, because forgetting is destructive in both: a stale fact the user corrected
    // and that is still remembered will be applied again next turn, and a fact nobody mentioned
    // that went with it is work the user did once and has to do again.
    private static IEnumerable<string> Forgot(Scenario scenario, Recording recording)
    {
        var remembered = recording.MemoriesAfter;

        var lingering = scenario.Remembered
            .Where(fact => fact.Forgotten && remembered.Contains(fact.Content, StringComparer.Ordinal))
            .Select(fact => $"'{fact.Content}' was to be forgotten and is still remembered");

        var swept = scenario.Remembered
            .Where(fact => !fact.Forgotten && !remembered.Contains(fact.Content, StringComparer.Ordinal))
            .Select(fact => $"'{fact.Content}' was forgotten and nothing asked for it");

        return [.. lingering, .. swept];
    }

    // Each declared task takes a delegation of its own, and every delegation has to answer to one:
    // two independent halves are two workers running at once, and one worker told to do both is
    // the sequence delegating was supposed to avoid — with a prompt that would satisfy both.
    private static IEnumerable<string> Delegated(Scenario scenario, Recording recording)
    {
        var unmatched = recording.Delegations.ToList();

        // A loop rather than a projection: each expectation consumes the delegation it matched, so
        // the state the next one reads is the state this one left.
        var missing = new List<string>();
        foreach (var expectation in scenario.Delegates)
        {
            // A delegation to the right profile that is missing something is consumed by the
            // expectation it was trying to answer, so it is reported once — as the context it left
            // out, rather than a second time as work nobody declared.
            var match = unmatched.FirstOrDefault(d => Answers(expectation, d));
            var attempt = match ?? unmatched.FirstOrDefault(d => SameProfile(expectation, d));
            if (attempt is not null)
            {
                unmatched.Remove(attempt);
            }

            if (match is null)
            {
                missing.Add(
                    $"nothing was delegated to '{expectation.Profile}' carrying " +
                    $"{string.Join(", ", expectation.Carries.Select(c => $"'{c}'"))}. " +
                    $"Delegated: {Describe(recording)}");
            }
        }

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
            (recording.FilesAfter.TryGetValue(expectation.Path, out var content), expectation.Deleted) switch
            {
                (true, true) => [$"{expectation.Path} was to be gone and is still there"],
                (true, false) =>
                    Says(expectation, content!, recording.FilesBefore.GetValueOrDefault(expectation.Path)),
                (false, true) => [],
                (false, false) => [$"{expectation.Path} does not exist after the turn"]
            });

    private static IEnumerable<string> Says(FileExpectation expectation, string content, string? before)
    {
        // Case is the model's: "Migración de la base de datos" at the head of a line carries the
        // 'migraci' a scenario asks for, and an ordinal check failed the append for capitalising.
        var missing = expectation.Contains
            .Where(text => !content.Contains(text, StringComparison.OrdinalIgnoreCase))
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
        var changed = recording.Moved;

        var undeclared = changed
            .Where(entry => !scenario.Changes.Any(c => c.Declares(entry)))
            .Select(entry =>
                $"{entry.Key} changed to '{entry.Value}' and the scenario did not declare it " +
                $"(it was '{recording.StateBefore.GetValueOrDefault(entry.Key) ?? "absent"}')");

        var missing = scenario.Changes
            .Where(change => !changed.Any(entry => change.Names(entry.Key) && entry.Value == change.To))
            .Select(change =>
                $"{change.Key} was to end at '{change.To}' and is at " +
                $"'{EndedAt(change, recording)}'");

        return [.. missing, .. undeclared];
    }

    // What a missed change is at now: the entity's own state, or for a pattern the matching
    // entities that moved — none of which reached the value, or the change would not be missing.
    private static string EndedAt(StateChange change, Recording recording) =>
        change.IsPattern
            ? string.Join(", ", recording.Moved.Where(e => change.Names(e.Key)).Select(e => $"{e.Key}={e.Value}"))
                is { Length: > 0 } moved ? moved : "absent"
            : recording.StateAfter.GetValueOrDefault(change.Key) ?? "absent";

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
            .Select(p => (
                Tool: new ToolPatternMatcher([p.Tool]),
                Path: new ToolPatternMatcher([p.Path]),
                Command: new ToolPatternMatcher([p.Command])))
            .ToList();

        // A refused attempt at a required call, corrected into it: the tool named the fix and the
        // next call at the same tool, in the same directory, is the one the scenario asks for. The
        // fix it named may be the body or the file's own name (a timer at /timers/te/te.json is
        // sent to timer.json beside it), so the place is what has to agree, not the whole path.
        // That round-trip is the refusal working, not a call the model should not have made; the
        // ceiling still counts it.
        var corrected = scenario.Required
            .Select(e => Considered(recording).FirstOrDefault(call => Matches(e, call)))
            .Where(landed => landed is not null)
            .SelectMany(landed => Considered(recording)
                .Where(call => call.Sequence < landed!.Sequence
                               && string.Equals(call.ToolName, landed.ToolName, StringComparison.Ordinal)
                               && string.Equals(Directory(Path(call)), Directory(Path(landed)), StringComparison.Ordinal)
                               && Refused(call)))
            .ToHashSet();

        return Considered(recording)
            .Where(call => !string.Equals(call.ToolName, EvalTools.Subagent, StringComparison.Ordinal))
            .Where(call => !corrected.Contains(call))
            .Where(call => !scenario.Required.Any(e => Matches(e, call))
                           && !permitted.Any(p => p.Tool.IsMatch(call.ToolName)
                                                  && p.Path.IsMatch(Path(call))
                                                  && p.Command.IsMatch(Command(call))))
            .Select(call =>
                $"unnecessary call: {call.ToolName} {call.Arguments} is neither required nor permitted");
    }

    // The virtual directory a path sits in: paths are mount-prefixed and '/'-separated, so the
    // last segment is the file (or the timer directory a create names in place of its file).
    private static string Directory(string path) =>
        path.LastIndexOf('/') is var slash && slash > 0 ? path[..slash] : "";

    // Nothing was written: the tool threw, or answered with its error envelope — the VFS shape
    // and the MCP one both carry the refusal in the result text.
    private static bool Refused(ToolInvocation call) =>
        call.Outcome != ToolInvocationOutcome.Completed
        || (call.Result is { } result
            && (Regex.IsMatch(result, @"""ok""\s*:\s*false") || Regex.IsMatch(result, @"""isError""\s*:\s*true")));

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

    // First in the list because it explains every other failure under it: a turn cut off partway
    // has whatever calls it had made and no reply, and reporting those as the model's choices
    // would be reporting a sentence nobody finished.
    private static IEnumerable<string> TimedOut(Recording recording) =>
        recording.TimedOut
            ? [$"the run timed out before the turn finished, after {recording.Calls.Count} calls; "
               + "what follows is a partial turn"]
            : recording.ProviderError is { } error
                ? [$"the provider failed the run after {recording.Calls.Count} calls: {error}; "
                   + "what follows is a partial turn"]
                : [];

    private static IEnumerable<string> OverCeiling(Scenario scenario, Recording recording) =>
        Considered(recording).Count() > scenario.CallCeiling
            ? [$"call ceiling exceeded: {Considered(recording).Count()} calls against a ceiling of " +
               $"{scenario.CallCeiling}. Seen: {Seen(recording)}"]
            : [];

    // Everything the scenario answers for. One call is left out: this model occasionally opens a
    // turn with a throwaway search and then does the work correctly — it is clearing its throat
    // rather than attempting the request, and it lands on whichever scenario happens to be
    // running, so counting it would turn one arbitrary scenario per run red. The behaviour itself
    // is recorded as a finding of its own, and the dump still lists the call.
    private static IEnumerable<ToolInvocation> Considered(Recording recording) =>
        recording.Calls.Where(call => !IsWarmUpProbe(call));

    // Originally the literal query "noop". The tic outlived that wording: later runs opened with
    // an empty query, "ignore", "site:example.com", "site:local Ziggurat" and the bare word
    // "timer" lifted from the turn — so matching on the words was a list that kept growing, and
    // "timer" reads like a question a real search might ask.
    //
    // What every probe shares is that it asks for a single result. Across every armed dump this
    // suite has produced, a genuine search asks for 5, 10 or 20 and never for 1: one result is
    // useless to a model that means to read what comes back, which is why the tic and the real
    // thing separate cleanly here and not on the query. A probe is still recorded and still
    // dumped; what it no longer does is redden whichever scenario it landed on.
    private static bool IsWarmUpProbe(ToolInvocation call)
    {
        // The browse shape of the same tic: a placeholder page fetched for one character.
        var budget = call.ToolName.EndsWith(WebSearchTool.Name, StringComparison.Ordinal) ? "maxResults"
            : call.ToolName.EndsWith(WebBrowseTool.Name, StringComparison.Ordinal) ? "maxLength"
            : null;
        if (budget is null)
        {
            return false;
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            return arguments.RootElement.TryGetProperty(budget, out var asked)
                   && asked.TryGetInt32(out var wanted)
                   && wanted <= 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CallExpectation Expectation(Scenario scenario, string label) =>
        scenario.Required.FirstOrDefault(e => e.Label == label)
        ?? throw new InvalidOperationException(
            $"Scenario '{scenario.Name}' orders '{label}', which is not one of its required calls.");

    private static ToolInvocation? Match(CallExpectation expectation, Recording recording) =>
        recording.Calls.FirstOrDefault(call => Matches(expectation, call));

    private static bool Matches(CallExpectation expectation, ToolInvocation call)
    {
        // By pattern rather than by equality: a tool served over MCP is named after the endpoint it
        // was dialled on, host and port, and the port is whatever was free when the stack came up.
        if (!new ToolPatternMatcher([expectation.Tool]).IsMatch(call.ToolName))
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

    // Empty for every tool that runs nothing, which the default `*` permission matches — so a
    // permission that says nothing about commands keeps meaning what it meant.
    private static string Command(ToolInvocation call)
    {
        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            return arguments.RootElement.TryGetProperty("command", out var command)
                   && command.ValueKind == JsonValueKind.String
                ? command.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
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