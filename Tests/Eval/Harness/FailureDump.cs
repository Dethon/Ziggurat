using System.Text;

namespace Tests.Eval.Harness;

// One self-contained file per failed run, in an output directory git ignores. Everything in it is
// something that is gone once the process ends and irrecoverable by re-running, because the next
// run of the same scenario against the same model is a different run.
public static class FailureDump
{
    // At the repository root rather than under bin: a dump is read by a person after the run, and
    // one that a rebuild deletes is one nobody gets to read. Git ignores the directory.
    public static string DefaultDirectory =>
        Path.Combine(RepositoryRoot(), ".eval-output");

    private static string RepositoryRoot() =>
        Ancestors(new DirectoryInfo(AppContext.BaseDirectory))
            .FirstOrDefault(d => File.Exists(Path.Combine(d.FullName, "Ziggurat.sln")))
            ?.FullName
        ?? AppContext.BaseDirectory;

    private static IEnumerable<DirectoryInfo> Ancestors(DirectoryInfo directory) =>
        directory.Parent is null ? [directory] : [directory, .. Ancestors(directory.Parent)];

    // Null when there is nothing to explain — a passing scenario archives nothing, so a green run
    // leaves the working tree exactly as it found it.
    public static string? Describe(
        string directory, Scenario scenario, Recording recording, string decoratedTurn,
        IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        var path = Write(directory, scenario, recording, decoratedTurn, failures);

        return $"""
                Scenario '{scenario.Name}' failed:
                  {string.Join("\n  ", failures)}

                Full dump:
                  {path}
                """;
    }

    public static string Write(
        string directory, Scenario scenario, Recording recording, string decoratedTurn,
        IReadOnlyList<string> failures)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Slug(scenario.Name)}-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, Render(scenario, recording, decoratedTurn, failures));
        return path;
    }

    private static string Render(
        Scenario scenario, Recording recording, string decoratedTurn, IReadOnlyList<string> failures)
    {
        var dump = new StringBuilder()
            .AppendLine($"# {scenario.Name}")
            .AppendLine()
            .AppendLine($"Agent: {scenario.AgentId}")
            .AppendLine($"Pinned instant: {scenario.Instant:O}")
            .AppendLine($"Claims: {string.Join(", ", scenario.Claims)}")
            .AppendLine()
            // The configured model is in the system prompt section below; what a routing surprise
            // is diagnosed from is the one that actually answered.
            .AppendLine($"Served model: {recording.Route?.Model ?? "unknown"}")
            .AppendLine($"Served provider: {recording.Route?.Provider ?? "unknown"}")
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