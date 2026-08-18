using System.Text;
using Infrastructure.Agents.ChatClients;

namespace Tests.Eval.Harness;

// One self-contained file per failed run, in an output directory git ignores. Everything in it is
// something that is gone once the process ends and irrecoverable by re-running, because the next
// run of the same scenario against the same model is a different run.
public static class FailureDump
{
    // At the repository root rather than under bin: a dump is read by a person after the run, and
    // one that a rebuild deletes is one nobody gets to read. Git ignores the directory.
    public static string DefaultDirectory =>
        Path.Combine(RepositoryRoot.Path, ".eval-output");

    // Null when there is nothing to explain — a passing scenario archives nothing, so a green run
    // leaves the working tree exactly as it found it.
    public static string? Describe(string directory, FailedRun run)
    {
        if (run.Failures.Count == 0)
        {
            return null;
        }

        var path = Write(directory, run);

        return $"""
                Scenario '{run.Scenario.Name}' failed:
                  {string.Join("\n  ", run.Failures)}

                Full dump:
                  {path}
                """;
    }

    public static string Write(string directory, FailedRun run)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Slug(run.Scenario.Name)}-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, Render(run));
        return path;
    }

    private static string Render(FailedRun run)
    {
        var (scenario, recording, decoratedTurn, route, failures) = run;
        var dump = new StringBuilder()
            .AppendLine($"# {scenario.Name}")
            .AppendLine()
            .AppendLine($"Agent: {scenario.AgentId}")
            .AppendLine($"Pinned instant: {scenario.Instant:O}")
            .AppendLine($"Claims: {string.Join(", ", scenario.Claims)}")
            .AppendLine()
            // The configured model is in the system prompt section below; what a routing surprise
            // is diagnosed from is the one that actually answered.
            .AppendLine($"Served model: {route?.Model ?? "unknown"}")
            .AppendLine($"Served provider: {route?.Provider ?? "unknown"}")
            .AppendLine()
            .AppendLine("## Failed assertions")
            .AppendLine()
            .AppendJoin("\n", failures.Select(f => $"- {f}"))
            .AppendLine()
            .AppendLine()
            .AppendLine("## Decorated turn")
            .AppendLine()
            .AppendLine("```")
            .AppendLine(decoratedTurn)
            .AppendLine("```")
            .AppendLine()
            .AppendLine("## Recorded calls")
            .AppendLine();

        if (recording.Calls.Count == 0)
        {
            dump.AppendLine("None.").AppendLine();
        }

        recording.Calls
            .Select(call => $"""
                             ### {call.Sequence}. {call.ToolName} ({call.Outcome})

                             Arguments:

                             ```json
                             {call.Arguments}
                             ```

                             Result:

                             ```
                             {call.Error ?? call.Result ?? "none"}
                             ```

                             """)
            .ToList()
            .ForEach(section => dump.AppendLine(section));

        var moved = recording.StateAfter
            .Where(entry => recording.StateBefore.GetValueOrDefault(entry.Key) != entry.Value)
            .Select(entry =>
                $"- {entry.Key}: " +
                $"{recording.StateBefore.GetValueOrDefault(entry.Key) ?? "absent"} → {entry.Value}")
            .ToList();

        // Only what moved. A dump listing every entity in the home would bury the one line that
        // says the scene the model ran turned three other things off.
        if (recording.StateBefore.Count > 0)
        {
            dump.AppendLine("## What moved in the home")
                .AppendLine()
                .AppendJoin("\n", moved.Count > 0 ? moved : ["- nothing"])
                .AppendLine()
                .AppendLine();
        }

        return dump
            .AppendLine("## Final reply")
            .AppendLine()
            .AppendLine("```")
            .AppendLine(recording.Reply)
            .AppendLine("```")
            .AppendLine()
            .AppendLine("## Assembled system prompt")
            .AppendLine()
            .AppendLine("```")
            .AppendLine(recording.SystemPrompt ?? "(none captured)")
            .AppendLine("```")
            .ToString();
    }

    private static string Slug(string name) =>
        string.Join("-", name.ToLowerInvariant().Split(
            [' ', '/', '\\', ':'], StringSplitOptions.RemoveEmptyEntries));
}

// One failed run, whole. The route is carried beside the recording rather than read off it: under
// k of N the provider name is resolved after the fact, and the run being explained is the one that
// failed rather than the last one taken.
public sealed record FailedRun(
    Scenario Scenario,
    Recording Recording,
    string DecoratedTurn,
    ServedRoute? Route,
    IReadOnlyList<string> Failures);