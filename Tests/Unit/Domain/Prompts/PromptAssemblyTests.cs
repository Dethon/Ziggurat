using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// The assembly's two jobs beyond ordering: reporting a section that outgrew what somebody budgeted
// for it, and reporting two sections that legislate the same thing without saying which one wins.
// Both are driven here on sections built for the test, so a real prompt changing its wording never
// makes this file pass or fail for the wrong reason.
public class PromptAssemblyTests
{
    private static PromptSection Section(
        string name,
        PromptPriority priority,
        string text = "text",
        int budget = 1_000,
        ConflictPolicy? conflict = null,
        PromptAudience? audience = null) =>
        new PromptDeclaration
        {
            Name = name,
            Purpose = "for the test",
            Priority = priority,
            TokenBudget = budget,
            Conflict = conflict ?? ConflictPolicy.None,
            Audience = audience ?? PromptAudience.Everyone
        }.Bind(text);

    [Fact]
    public void Build_SectionsOutOfOrder_AreReadInPriorityOrder()
    {
        var assembly = PromptAssembly.Build("agent",
        [
            Section("last", PromptPriority.Language, "LANGUAGE"),
            Section("first", PromptPriority.CoreDirective, "CORE"),
            Section("middle", PromptPriority.Client, "CLIENT")
        ]);

        assembly.Sections.Select(s => s.Name).ShouldBe(["first", "middle", "last"]);
        assembly.Text.ShouldBe("CORE\n\nCLIENT\n\nLANGUAGE");
    }

    // Stable within a band, which is what keeps several servers' prompts in the order their
    // endpoints were configured in rather than in whatever order a sort found them.
    [Fact]
    public void Build_TwoSectionsInOneBand_KeepTheOrderTheyWereOfferedIn()
    {
        var assembly = PromptAssembly.Build("agent",
        [
            Section("b", PromptPriority.Client),
            Section("a", PromptPriority.Client)
        ]);

        assembly.Sections.Select(s => s.Name).ShouldBe(["b", "a"]);
    }

    [Fact]
    public void Build_ASectionForOtherAgents_IsNotAssembled()
    {
        var assembly = PromptAssembly.Build("jonas",
        [
            Section("theirs", PromptPriority.Client, audience: PromptAudience.ForAgents("nabu")),
            Section("everyones", PromptPriority.Client)
        ]);

        assembly.Sections.Select(s => s.Name).ShouldBe(["everyones"]);
    }

    [Fact]
    public void Build_AnEmptySection_IsNotAssembled()
    {
        PromptAssembly.Build("agent", [Section("blank", PromptPriority.Client, text: "   ")])
            .Sections.ShouldBeEmpty();
    }

    [Fact]
    public void Build_ASectionOverItsBudget_IsReported()
    {
        var assembly = PromptAssembly.Build("agent",
            [Section("fat", PromptPriority.Client, text: new string('x', 4_000), budget: 100)]);

        assembly.Warnings.ShouldContain(w => w.Contains("'fat'") && w.Contains("budget of 100"));
    }

    [Fact]
    public void Build_TwoSectionsGoverningOneRuleWithNoWinner_IsReported()
    {
        var assembly = PromptAssembly.Build("agent",
        [
            Section("a", PromptPriority.Feature, conflict: ConflictPolicy.Governs(PromptRules.Formatting)),
            Section("b", PromptPriority.Client, conflict: ConflictPolicy.Governs(PromptRules.Formatting))
        ]);

        assembly.Warnings.ShouldContain(w =>
            w.Contains("'a'") && w.Contains("'b'") && w.Contains("formatting"));
    }

    [Fact]
    public void Build_TheLaterSectionDeclaringItOverridesTheEarlier_IsNotReported()
    {
        var assembly = PromptAssembly.Build("agent",
        [
            Section("a", PromptPriority.Feature, conflict: ConflictPolicy.Governs(PromptRules.Formatting)),
            Section("b", PromptPriority.ChannelOverride,
                conflict: ConflictPolicy.Governs(PromptRules.Formatting).Beating("a"))
        ]);

        assembly.Warnings.ShouldBeEmpty();
    }

    // An override is a claim about where a section is read, so it can be false. A section that says
    // it wins from above loses in fact, and silently — the model applies what it read last.
    [Fact]
    public void Build_ASectionClaimingToOverrideOneReadAfterIt_IsReported()
    {
        var assembly = PromptAssembly.Build("agent",
        [
            Section("early", PromptPriority.CoreDirective,
                conflict: ConflictPolicy.Governs(PromptRules.Verbosity).Beating("late")),
            Section("late", PromptPriority.Language,
                conflict: ConflictPolicy.Governs(PromptRules.Verbosity))
        ]);

        assembly.Warnings.ShouldContain(w =>
            w.Contains("'early'") && w.Contains("overrides 'late'") && w.Contains("before it"));
    }

    [Fact]
    public void Build_SectionsGoverningDifferentRules_AreNotAContradiction()
    {
        PromptAssembly.Build("agent",
        [
            Section("a", PromptPriority.Feature, conflict: ConflictPolicy.Governs(PromptRules.Memory)),
            Section("b", PromptPriority.Client, conflict: ConflictPolicy.Governs(PromptRules.Formatting))
        ]).Warnings.ShouldBeEmpty();
    }
}