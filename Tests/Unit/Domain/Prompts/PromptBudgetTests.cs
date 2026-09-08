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
        Fits(PromptManifest.Bind(PromptManifest.CoreDirective, CoreDirectivePrompt.Instructions));

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

    // The ceiling is what the largest agent's sections are budgeted to, plus a fixed headroom. The
    // first figure is a ratchet: it is the largest declared sum rounded up to the hundred, so a
    // section that moves behind a skill lowers it by editing one number, and a figure left where
    // it was — room for the prompt to grow back into — is what this reports.
    [Fact]
    public void Budget_TheStandingFigure_IsTheLargestAgentsDeclaredSectionsRoundedUp()
    {
        var largest = AgentPromptFixture.SnapshotIds
            .Select(AgentPromptFixture.Assemble)
            .Max(assembly => assembly.Sections.Sum(s => s.Declaration.TokenBudget));

        PromptManifest.StandingTokens.ShouldBe(
            (largest + 99) / 100 * 100,
            $"the largest agent's sections are budgeted at {largest} tokens; set StandingTokens to " +
            "that rounded up to the hundred, so the ceiling follows what remains");
    }

    // The check the ceiling test makes, shown failing: lower the standing figure below what an
    // agent's sections add up to, and that agent no longer fits — which is how a move that forgot
    // to lower it stays honest, and a section that grew back is caught.
    [Fact]
    public void Budget_LoweringTheStandingFigureBelowAnAgentsSections_FailsThatAgent()
    {
        var declared = AgentPromptFixture.Assemble("nabu").Sections.Sum(s => s.Declaration.TokenBudget);

        PromptManifest.FitsCeiling(declared).ShouldBeTrue();
        PromptManifest.FitsCeiling(declared, standingTokens: declared - PromptManifest.Headroom - 1)
            .ShouldBeFalse();
    }

    private static void Fits(PromptSection section) =>
        section.IsOverBudget.ShouldBeFalse(
            $"'{section.Name}' is {section.TokenCount} tokens against a budget of " +
            $"{section.Declaration.TokenBudget}. Either trim it or raise the budget in PromptManifest " +
            "and say why.");
}