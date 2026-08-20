using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// A prompt costs money and context on every turn of every conversation, and it only ever grows:
// each addition is small, reasonable and invisible. A budget is where somebody wrote down what a
// section is worth, and this is what makes exceeding it a decision instead of a drift.
public class PromptBudgetTests
{
    public static TheoryData<string> ServedSections =>
        [.. AgentPromptFixture.ServedText.Keys];

    public static TheoryData<string> FeatureSections =>
        [.. AgentPromptFixture.FeatureText.Keys];

    public static TheoryData<string> Agents => [.. AgentPromptFixture.SnapshotIds];

    [Theory]
    [MemberData(nameof(ServedSections))]
    public void Budget_EachServerPrompt_FitsItsDeclaredBudget(string name)
    {
        Fits(PromptManifest.Bind(name, AgentPromptFixture.ServedText[name]));
    }

    [Theory]
    [MemberData(nameof(FeatureSections))]
    public void Budget_EachFeaturePrompt_FitsItsDeclaredBudget(string name)
    {
        Fits(PromptManifest.Bind(name, AgentPromptFixture.FeatureText[name]));
    }

    [Fact]
    public void Budget_TheVoiceSection_FitsItsDeclaredBudget()
    {
        Fits(PromptManifest.Selected(VoicePrompt.Name)!);
    }

    [Fact]
    public void Budget_TheCoreDirectiveAndEveryLanguageTemplate_FitTheirDeclaredBudgets()
    {
        Fits(PromptManifest.Bind(PromptManifest.CoreDirective, BasePrompt.Instructions));

        foreach (var language in (string[])["es", "en", "Galician"])
        {
            Fits(PromptManifest.Bind(PromptManifest.Language, LanguagePrompt.Build(language)!));
        }
    }

    // The ceiling that matters to a turn rather than to a section: everything here is re-sent on
    // every request, and what it does not take is what the conversation itself gets.
    [Theory]
    [MemberData(nameof(Agents))]
    public void Budget_AnAgentsWholePrompt_FitsTheAgentCeiling(string agentId)
    {
        var assembly = AgentPromptFixture.Assemble(agentId);

        assembly.TokenCount.ShouldBeLessThanOrEqualTo(
            PromptManifest.MaxAgentPromptTokens,
            $"'{agentId}' assembles {assembly.TokenCount} tokens of system prompt: " +
            string.Join(", ", assembly.Sections
                .OrderByDescending(s => s.TokenCount)
                .Take(5)
                .Select(s => $"{s.Name} {s.TokenCount}")));
    }

    // The budgets are only worth what they add up to for one agent: a table where every entry is
    // generous buys nothing. Summed over every declaration would be the wrong number — no agent
    // gets every section, and the download assistant and the house guide never meet — so this is
    // each agent's own worst case, the prompt it would have if every section it does assemble sat
    // exactly on its budget.
    [Theory]
    [MemberData(nameof(Agents))]
    public void Budget_AnAgentsSectionsAtTheirBudgets_StillFitTheCeiling(string agentId)
    {
        var assembly = AgentPromptFixture.Assemble(agentId);

        assembly.Sections.Sum(s => s.Declaration.TokenBudget)
            .ShouldBeLessThanOrEqualTo(
                PromptManifest.MaxAgentPromptTokens,
                $"the sections '{agentId}' assembles are budgeted for more than the ceiling allows");
    }

    private static void Fits(PromptSection section) =>
        section.IsOverBudget.ShouldBeFalse(
            $"'{section.Name}' is {section.TokenCount} tokens against a budget of " +
            $"{section.Declaration.TokenBudget}. Either trim it or raise the budget in PromptManifest " +
            "and say why.");
}