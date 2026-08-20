using Domain.Prompts;
using Shouldly;

namespace Tests.Eval;

// What makes an untested rule visible rather than assumed. It costs nothing to run and needs no
// model, so it runs on a bare invocation like any other test: the whole point is that adding a
// rule to a prompt costs either a scenario or a line in the exemption list, and a check that only
// ran when somebody armed the eval would not collect that.
public class ClaimCoverageTests
{
    // A judged check is a citation too: it aims a rubric at a claim, so the claim is tested —
    // by a verdict rather than a matcher, which the scorecard's coverage field distinguishes.
    // So is a conditional one: it tests the claim on every run that produces its material.
    private static HashSet<string> Cited() =>
        EvalSuite.All.SelectMany(Cites).ToHashSet();

    private static IEnumerable<string> Cites(Harness.Scenario scenario) =>
        scenario.Claims
            .Concat(scenario.Judged.Select(check => check.Claim))
            .Concat(scenario.IfDelegated.SelectMany(condition =>
                condition.Claims.Concat(condition.Judged.Select(check => check.Claim))));

    [Fact]
    public void EveryDeclaredClaim_HasEitherAScenarioOrAnExemption()
    {
        var cited = Cited();

        var uncovered = PromptManifest.Claims
            .Select(claim => claim.Id)
            .Where(id => !cited.Contains(id) && !ClaimExemptions.Reasons.ContainsKey(id))
            .ToList();

        uncovered.ShouldBeEmpty(
            "these claims are declared but nothing tests them and nothing excuses them: " +
            string.Join(", ", uncovered));
    }

    [Fact]
    public void EveryCitedClaim_Exists()
    {
        var declared = PromptManifest.Claims.Select(c => c.Id).ToHashSet();

        var invented = EvalSuite.All
            .SelectMany(s => Cites(s).Select(claim => (s.Name, Claim: claim)))
            .Where(cite => !declared.Contains(cite.Claim))
            .Select(cite => $"'{cite.Name}' cites '{cite.Claim}'")
            .ToList();

        invented.ShouldBeEmpty(
            "a scenario cites a claim no section declares: " + string.Join(", ", invented));
    }

    [Fact]
    public void EveryConditionalDelegation_NamesAProfileTheScenarioTolerates()
    {
        // A condition on a profile the scenario neither tolerates nor requires can never trigger
        // without the run failing as an undeclared delegation first — a condition that looks
        // wired and asserts nothing, which the compiler cannot see.
        var untriggerable = EvalSuite.All
            .SelectMany(s => s.IfDelegated
                .Where(condition =>
                    !s.MayDelegateTo.Contains(condition.Profile, StringComparer.OrdinalIgnoreCase)
                    && !s.Delegates.Any(d => string.Equals(
                        d.Profile, condition.Profile, StringComparison.OrdinalIgnoreCase)))
                .Select(condition => $"'{s.Name}' conditions on '{condition.Profile}'"))
            .ToList();

        untriggerable.ShouldBeEmpty(
            "a conditional delegation names a profile the scenario would fail for using: " +
            string.Join(", ", untriggerable));
    }

    [Fact]
    public void EveryExemption_NamesADeclaredClaim_AndGivesAReason()
    {
        var declared = PromptManifest.Claims.Select(c => c.Id).ToHashSet();

        foreach (var (id, exemption) in ClaimExemptions.Reasons)
        {
            declared.ShouldContain(id, $"'{id}' is excused but no section declares it");
            exemption.Reason.ShouldNotBeNullOrWhiteSpace($"'{id}' is excused with no reason");
        }
    }

    [Fact]
    public void NoClaim_IsBothCoveredAndExcused()
    {
        // The exemption list is the backlog. A claim that has a scenario and is still excused
        // means the backlog is lying about how much is left.
        ClaimExemptions.Reasons.Keys.Where(Cited().Contains).ShouldBeEmpty();
    }
}