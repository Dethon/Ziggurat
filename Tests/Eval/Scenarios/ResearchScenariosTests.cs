using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// What makes the family a measurement: every rung is marked on the same dimensions over the same
// number of runs, so two models' scorecards are two columns of one table.
public class ResearchScenariosTests
{
    [Fact]
    public void EveryRung_IsMarkedOnTheSameThreeDimensions()
    {
        ResearchScenarios.All.ShouldAllBe(scenario =>
            scenario.Graded.Select(grade => grade.Dimension)
                .SequenceEqual(new[] { "accuracy", "grounding", "reasoning" }));
    }

    [Fact]
    public void EveryRung_TakesEveryRunItDeclares()
    {
        // Stopping at the threshold is right for a contract and wrong for a mean: a rung that
        // stopped at its second green would be scored over two runs on a good model and three
        // on a worse one.
        ResearchScenarios.All.ShouldAllBe(scenario => scenario.Policy.Exhaustive && scenario.Policy.N >= 3);
    }

    [Fact]
    public void ARung_EitherResearchesAlone_OrMayHandTheWorkToARealWorker()
    {
        // Never a canned worker: a research answer that came back as a stub's one line is marked
        // as no research, for a decision the prompt allows.
        ResearchScenarios.All.ShouldAllBe(scenario => scenario.Workers != WorkerMode.Canned);
        ResearchScenarios.All.Where(scenario => scenario.Workers == WorkerMode.None)
            .ShouldAllBe(scenario => scenario.MayDelegateTo.Count == 0);
        ResearchScenarios.All.Where(scenario => scenario.Workers == WorkerMode.Real)
            .ShouldAllBe(scenario => scenario.MayDelegateTo.Contains("jonas-worker"));
        ResearchScenarios.All.Count(scenario => scenario.Workers == WorkerMode.Real).ShouldBe(2);
    }

    [Fact]
    public void TheFullTierAndTheSmokeTier_TakeNoneOfThem()
    {
        var research = ResearchScenarios.All.Select(scenario => scenario.Name).ToHashSet();

        Names(EvalTier.Full).ShouldNotContain(name => research.Contains(name));
        Names(EvalTier.Smoke).ShouldNotContain(name => research.Contains(name));
        Names(EvalTier.Research).ShouldBe(research, ignoreOrder: true);
    }

    private static List<string> Names(EvalTier tier) =>
        [.. ((IEnumerable<object[]>)EvalSuite.Named(tier)).Select(row => (string)row[0])];
}