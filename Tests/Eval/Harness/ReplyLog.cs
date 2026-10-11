using System.Text;

namespace Tests.Eval.Harness;

// What a graded run said, beside what it was marked: one file per run, under the model that
// answered. The failure dump keeps a reply only when a run fails its contract, and a graded run
// rarely does — so a mark on the scorecard had nothing beside it to be checked against, and a
// reader asking why a reply scored four had the judge's sentence and not the reply.
public static class ReplyLog
{
    public static string Write(
        string directory, Scenario scenario, Recording recording, IReadOnlyList<GradeMark> marks, int run)
    {
        var folder = Path.Combine(directory, "replies", FailureDump.Slug(recording.Route?.Model ?? "unknown"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{FailureDump.Slug(scenario.Name)}-run{run}.md");

        var log = new StringBuilder()
            .AppendLine($"# {scenario.Name} — run {run}")
            .AppendLine()
            .AppendLine($"Model: {recording.Route?.Model ?? "unknown"}")
            .AppendLine($"Calls: {ScenarioChecks.Calls(recording)}")
            .AppendLine()
            .AppendLine("## Turn")
            .AppendLine()
            .AppendLine(scenario.Turn.Text)
            .AppendLine()
            .AppendLine("## Marks")
            .AppendLine()
            .AppendJoin("\n", marks.Count == 0
                ? ["- none"]
                : marks.Select(mark => mark.Reason is null
                    ? $"- {mark.Dimension}: {mark.Score:0.#}"
                    : $"- {mark.Dimension}: {mark.Score:0.#} — {mark.Reason}"))
            .AppendLine()
            .AppendLine()
            .AppendLine("## Reply")
            .AppendLine()
            .AppendLine(recording.TimedOut
                ? "(the run timed out before it answered)"
                : recording.ProviderError is { } error
                    ? $"(the provider failed the run: {error})"
                    : recording.Reply);

        File.WriteAllText(path, log.ToString());
        return path;
    }
}