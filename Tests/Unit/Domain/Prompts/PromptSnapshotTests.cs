using System.Text;
using Domain.Prompts;
using Infrastructure.Agents.Skills;
using Infrastructure.Utils;
using Microsoft.Agents.AI;
using Shouldly;
using Tests.Unit.Infrastructure.Helpers;

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

    public static TheoryData<string> Skills => [.. AgentPromptFixture.ServedSkills.Keys];

    [Theory]
    [MemberData(nameof(Agents))]
    public async Task Snapshot_AgentPrompt_MatchesTheCommittedOne(string agentId)
    {
        var rendered = await RenderAsync(AgentPromptFixture.Assemble(agentId), agentId);

        Check(AgentPromptFixture.SnapshotPath(agentId), rendered, agentId);
    }

    // One per skill, of the body as the server serves it — frontmatter and all — with the two
    // costs on top: what every turn pays to advertise it and what one load pays to read it. A body
    // that grows is a diff somebody reads, the same as a section's.
    [Theory]
    [MemberData(nameof(Skills))]
    public void Snapshot_SkillBody_MatchesTheCommittedOne(string name)
    {
        var skill = AgentPromptFixture.ServedSkills[name];
        var rendered = new StringBuilder()
            .AppendLine(
                $"# {name} — description {skill.DescriptionTokens} / {skill.Declaration.DescriptionBudget} tokens, " +
                $"body {skill.BodyTokens} / {skill.Declaration.BodyBudget} tokens, served by {skill.Declaration.ServedBy}")
            .AppendLine()
            .AppendLine(new string('=', 96))
            .AppendLine()
            .Append(SkillServerResources.Body(new SkillText(name, skill.Description, skill.Body)))
            .ToString();

        Check(AgentPromptFixture.SkillSnapshotPath(name), rendered, name);
    }

    private static void Check(string path, string rendered, string subject)
    {
        if (Environment.GetEnvironmentVariable("UPDATE_PROMPT_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rendered);
            return;
        }

        File.Exists(path).ShouldBeTrue(
            $"No snapshot for '{subject}'. Run UPDATE_PROMPT_SNAPSHOTS=1 dotnet test and review the file.");

        // Line endings are the checkout's business, not the prompt's.
        Normalize(File.ReadAllText(path)).ShouldBe(
            Normalize(rendered),
            $"The snapshot for '{subject}' has changed. If the change is intended, " +
            "regenerate with UPDATE_PROMPT_SNAPSHOTS=1 dotnet test.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    // The whole prompt, which is the composed sections and then whatever the framework appends
    // for the skills: rendered through the real provider, so the snapshot is what the model sees
    // and not this test's idea of it.
    private static async Task<string> RenderAsync(PromptAssembly assembly, string agentId)
    {
        var header = new StringBuilder()
            .AppendLine(
                $"# {agentId} — {assembly.TokenCount} tokens across {assembly.Sections.Count} sections" +
                (assembly.Skills.Count > 0 ? $" and {assembly.Skills.Count} skills" : ""))
            .AppendLine();

        foreach (var section in assembly.Sections)
        {
            header.AppendLine(
                $"  {section.Name,-24} {section.Priority,-18} " +
                $"{section.TokenCount,6} / {section.Declaration.TokenBudget,-6} {section.Declaration.Purpose}");
        }

        foreach (var skill in assembly.Skills)
        {
            header.AppendLine(
                $"  {skill.Name,-24} {"Skill",-18} " +
                $"{skill.DescriptionTokens,6} / {skill.Declaration.DescriptionBudget,-6} " +
                $"body {skill.BodyTokens} / {skill.Declaration.BodyBudget}, served by {skill.Declaration.ServedBy}");
        }

        foreach (var warning in assembly.Warnings)
        {
            header.AppendLine($"  !! {warning}");
        }

        var text = header
            .AppendLine()
            .AppendLine(new string('=', 96))
            .AppendLine()
            .Append(assembly.Text);

        if (assembly.Skills.Count > 0)
        {
            text.Append('\n').Append(await Advertisement(assembly.Skills));
        }

        return text.AppendLine().ToString();
    }

    private static Task<string> Advertisement(IReadOnlyList<PromptSkill> skills) =>
        SkillsProvider.AdvertisementAsync(skills, new ChatClientAgent(new FakeChatClient()));
}