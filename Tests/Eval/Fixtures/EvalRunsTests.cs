using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// A pass stops a scenario at its threshold unless asked not to: the exhaustive switch is what a
// model-bump diff sets, so that every rate on the two scorecards is over the same N.
public class EvalRunsTests
{
    [Fact]
    public void Applied_NothingAsked_LeavesTheDeclaredPolicyAlone()
    {
        var declared = new RunPolicy(2, 3);

        EvalRuns.Applied(declared, asked: null).ShouldBeSameAs(declared);
        EvalRuns.Applied(declared, asked: "0").ShouldBeSameAs(declared);
        EvalRuns.Applied(declared, asked: " ").ShouldBeSameAs(declared);
    }

    [Fact]
    public void Applied_Asked_TakesEveryDeclaredRun()
    {
        var applied = EvalRuns.Applied(new RunPolicy(2, 3), asked: "1");

        applied.Exhaustive.ShouldBeTrue();
        applied.Width.ShouldBe(3);
        applied.K.ShouldBe(2);
        applied.N.ShouldBe(3);
    }

    [Fact]
    public void Applied_KeepsAWidthTheScenarioNarrowedItself()
    {
        EvalRuns.Applied(new RunPolicy(2, 4) { Width = 1 }, asked: "true").Width.ShouldBe(1);
    }
}