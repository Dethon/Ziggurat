namespace Domain.Prompts;

// The prose that explains the advertised list, in the base prompt of every agent that has a skill
// (Sections/skills.md).
public static class SkillsPrompt
{
    public static readonly string Instructions = PromptDocuments.Section(PromptManifest.SkillsSection).Text;
}