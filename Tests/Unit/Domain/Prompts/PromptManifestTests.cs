using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// The manifest's own rules. A declaration is data, so nothing about it fails to compile: a budget
// of zero, a purpose nobody wrote, an override pointing at a section that no longer exists or at
// one further down the prompt — each of those is a table that reads fine and means nothing.
public class PromptManifestTests
{
    [Fact]
    public void Declarations_AreUniqueByName()
    {
        var duplicates = PromptManifest.Declarations
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        // Two servers answering to the same prompt name is the case this caught: `system_prompt`
        // from both the websearch and the library server, which no name-keyed budget can tell apart.
        duplicates.ShouldBeEmpty();
    }

    [Fact]
    public void Declarations_EachOne_SaysWhatItIsForAndWhatItMayCost()
    {
        foreach (var declaration in PromptManifest.Declarations)
        {
            declaration.Purpose.ShouldNotBeNullOrWhiteSpace($"'{declaration.Name}' has no purpose");
            declaration.Purpose.ShouldNotContain("\n", Case.Sensitive, $"'{declaration.Name}'");
            declaration.TokenBudget.ShouldBeGreaterThan(0, $"'{declaration.Name}'");
        }
    }

    [Fact]
    public void Declarations_EveryOverride_NamesADeclaredSectionThatIsReadEarlier()
    {
        var overrides = PromptManifest.Declarations
            .SelectMany(d => d.Conflict.Overrides.Select(name => (Winner: d, Loser: name)));

        foreach (var (winner, loserName) in overrides)
        {
            var loser = PromptManifest.Find(loserName);

            loser.ShouldNotBeNull($"'{winner.Name}' overrides '{loserName}', which is not declared");
            // Later is what the model applies, so a section can only override what it is read after.
            ((int)winner.Priority).ShouldBeGreaterThan(
                (int)loser.Priority,
                $"'{winner.Name}' overrides '{loserName}' but is read before it");
        }
    }

    [Fact]
    public void Declarations_EverySelectableSection_IsDeclared()
    {
        foreach (var name in PromptManifest.SelectableSections)
        {
            PromptManifest.Find(name).ShouldNotBeNull($"'{name}' is selectable but not declared");
            PromptManifest.Selected(name).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Selected_ANameNothingDeclares_ResolvesToNothing()
    {
        PromptManifest.Selected("no_such_section").ShouldBeNull();
    }

    [Fact]
    public void Validate_AnAgentNamingAnUndeclaredSection_Throws()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => PromptManifest.Validate([("nabu", ["voice"]), ("jack", ["pirate_rules"])]));

        ex.Message.ShouldContain("pirate_rules");
        ex.Message.ShouldContain("jack");
    }

    [Fact]
    public void Validate_EverySectionDeclared_Passes()
    {
        Should.NotThrow(() => PromptManifest.Validate([("nabu", [VoicePrompt.Name])]));
    }

    [Fact]
    public void Bind_APromptNoServerDeclared_IsAssembledAndReported()
    {
        var section = PromptManifest.Bind("surprise_prompt", "hello");

        section.Declaration.Declared.ShouldBeFalse();
        PromptAssembly.Build("any-agent", [section])
            .Warnings.ShouldContain(w => w.Contains("surprise_prompt") && w.Contains("not declared"));
    }

    [Fact]
    public void BindSkill_ASkillNoServerDeclared_IsOfferedAndReported()
    {
        var skill = PromptManifest.BindSkill("surprise-skill", "Does surprising things.", "# Surprise\n\nBoo.");

        skill.Declaration.Declared.ShouldBeFalse();
        skill.Declaration.BodyBudget.ShouldBe(PromptManifest.UndeclaredSkillBodyBudget);
        PromptAssembly.Build("any-agent", [], [skill])
            .Warnings.ShouldContain(w => w.Contains("surprise-skill") && w.Contains("not declared"));
    }

    [Fact]
    public void Build_ASkillOverItsBodyBudget_IsReported()
    {
        var declaration = PromptManifest.FindSkill(HomeWatchesSkill.Name).ShouldNotBeNull();
        var skill = declaration.Bind(HomeWatchesSkill.Description, new string('x', (declaration.BodyBudget + 1) * 4));

        PromptAssembly.Build("any-agent", [], [skill])
            .Warnings.ShouldContain(w => w.Contains(HomeWatchesSkill.Name) && w.Contains("over its budgets"));
    }

    [Fact]
    public void Skills_EachOne_IsServedByAServerAndDescribedInOneLine()
    {
        foreach (var skill in PromptManifest.Skills)
        {
            skill.ServedBy.ShouldStartWith("mcp-", Case.Sensitive, $"'{skill.Name}' names no compose service");
            skill.Description.ShouldNotContain("\n", Case.Sensitive, $"'{skill.Name}'");
            skill.DescriptionBudget.ShouldBeGreaterThan(0);
            skill.BodyBudget.ShouldBeGreaterThan(0);
            PromptManifest.Find(skill.Name).ShouldBeNull($"'{skill.Name}' is both a section and a skill");
        }
    }
}