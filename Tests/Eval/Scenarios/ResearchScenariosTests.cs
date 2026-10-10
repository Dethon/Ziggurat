using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// What makes the family a measurement: every rung is marked on the same dimensions over the same
// number of runs, so two models' scorecards are two columns of one table.
public class ResearchScenariosTests
{
    [Fact]
    public void EveryRung_IsMarkedOnTheSameThreeDimensions_AndMayAddItsOwn()
    {
        // The three are what makes the rungs one table. A rung may be marked on something more —
        // the medical one on whether the advice can be acted on — and that column is its own.
        ResearchScenarios.All.ShouldAllBe(scenario =>
            scenario.Graded.Select(grade => grade.Dimension).Take(3)
                .SequenceEqual(new[] { "accuracy", "grounding", "reasoning" }));
    }

    [Fact]
    public void TheDiabetesRung_IsMarkedOnAdvice_AndCountsAnUncalledForHospitalAsAReferral()
    {
        var rung = ResearchScenarios.All.Single(scenario => scenario.Name.Contains("diabetes"));

        rung.Turn.Text.ShouldContain("hasta el lunes");
        // The person's own numbers are in the turn: the dose is theirs to be worked out, and
        // the emergency number is not, so that naming it is something a reply does by itself.
        rung.Turn.Text.ShouldContain(Fixtures.SickDayCorpus.Es(Fixtures.SickDayCorpus.UserKetones));
        rung.Turn.Text.ShouldContain($"{Fixtures.SickDayCorpus.UserDailyTotal} unidades");
        rung.Turn.Text.ShouldNotContain("112");

        var actionable = rung.Graded.OfType<JudgedGrade>().Single(grade => grade.Dimension == "actionability");
        actionable.Rubric.ShouldContain("is not a deferral");
        // Both ways of not answering: the generic line, and sending somebody the plan keeps at
        // home to a hospital they said they cannot reach.
        actionable.Rubric.ShouldContain("consulta a tu médico");
        actionable.Rubric.ShouldContain("is a deferral too");
    }

    [Fact]
    public void TheMedicalRung_IsMarkedOnWhetherItsAdviceCanBeActedOn()
    {
        var rung = ResearchScenarios.All.Single(scenario => scenario.Name.Contains("sprain"));

        // The turn says a doctor is out of reach, in so many words: what is being marked is an
        // answer to a person who already knows they should see one.
        rung.Turn.Text.ShouldContain("hasta el lunes");
        var actionable = rung.Graded.OfType<JudgedGrade>().Single(grade => grade.Dimension == "actionability");
        actionable.Rubric.ShouldContain("consulta a tu médico");
        // A warning sign with what to do about it is the opposite of a deferral, and the rubric
        // has to say so or the judge marks down the best part of a good answer.
        actionable.Rubric.ShouldContain("is not a deferral");
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