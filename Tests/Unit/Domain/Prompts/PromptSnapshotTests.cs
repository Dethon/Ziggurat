using System.Text;
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// The whole prompt of each agent, written down. A prompt regression is otherwise the least
// diagnosable kind of change there is: nothing fails, the model just answers differently a week
// later, and the diff that did it was a wording tweak inside a server nobody was looking at. With
// the snapshot in the repo the change is in the pull request that caused it, with the token cost
// of every section beside it.
//
// Regenerate with UPDATE_PROMPT_SNAPSHOTS=1 dotnet test, then read the diff before committing it —
// that reading is the review this file exists for.
public class PromptSnapshotTests
{
    public static TheoryData<string> Agents => [.. AgentPromptFixture.SnapshotIds];

    [Theory]
    [MemberData(nameof(Agents))]
    public void Snapshot_AgentPrompt_MatchesTheCommittedOne(string agentId)
    {
        var rendered = Render(AgentPromptFixture.Assemble(agentId), agentId);
        var path = AgentPromptFixture.SnapshotPath(agentId);

        if (Environment.GetEnvironmentVariable("UPDATE_PROMPT_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rendered);
            return;
        }

        File.Exists(path).ShouldBeTrue(
            $"No snapshot for '{agentId}'. Run UPDATE_PROMPT_SNAPSHOTS=1 dotnet test and review the file.");

        // Line endings are the checkout's business, not the prompt's.
        Normalize(File.ReadAllText(path)).ShouldBe(
            Normalize(rendered),
            $"The assembled prompt for '{agentId}' has changed. If the change is intended, " +
            "regenerate with UPDATE_PROMPT_SNAPSHOTS=1 dotnet test.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string Render(PromptAssembly assembly, string agentId)
    {
        var header = new StringBuilder()
            .AppendLine($"# {agentId} — {assembly.TokenCount} tokens across {assembly.Sections.Count} sections")
            .AppendLine();

        foreach (var section in assembly.Sections)
        {
            header.AppendLine(
                $"  {section.Name,-24} {section.Priority,-18} " +
                $"{section.TokenCount,6} / {section.Declaration.TokenBudget,-6} {section.Declaration.Purpose}");
        }

        foreach (var warning in assembly.Warnings)
        {
            header.AppendLine($"  !! {warning}");
        }

        return header
            .AppendLine()
            .AppendLine(new string('=', 96))
            .AppendLine()
            .Append(assembly.Text)
            .AppendLine()
            .ToString();
    }
}