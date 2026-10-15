using Domain.Prompts;
using Shouldly;
using Tests.Eval.Harness;
using Tests.Eval.Sugary;

namespace Tests.Eval.Scenarios;

// The sugary agent is an estimator with nothing to call, so its scenarios tolerate no call; her log
// is Jonas's, so the scenario about reading it runs on him and stays out of the tier that scores a
// model for estimating.
public class SugaryScenariosTests
{
    [Fact]
    public void TheSugaryAgentsScenarios_PermitNoCallAtAll()
    {
        var estimates = SugaryScenarios.All.Where(s => s.AgentId == SugaryBenchmark.Agent).ToList();

        estimates.Count.ShouldBe(3);
        estimates.ShouldAllBe(s => s.Required.Count == 0 && s.Permitted.Count == 0 && s.CallCeiling == 0);
    }

    [Fact]
    public void ABenchmarkMeal_IsCountedAgainstNoCall()
    {
        var meal = new SugaryCase { Name = "a plate", Set = "nvr", Photos = ["photos/a.jpg"], Carbs = 1, Grams = 1 };

        SugaryBenchmark.ScenarioOf(meal, 1).CallCeiling.ShouldBe(0);
    }

    [Fact]
    public void TheLogScenario_IsJonass_AndOutsideTheSugaryTier()
    {
        var log = SugaryScenarios.HerLogIsReadThroughTheSkill;

        log.AgentId.ShouldBe("jonas");
        log.Claims.ShouldBe([SugarySkill.LoadsForAQuestionAboutHerLog.Id]);
        Names(EvalTier.Sugary).ShouldNotContain(log.Name);
        Names(EvalTier.Full).ShouldContain(log.Name);
    }

    private static IEnumerable<string> Names(EvalTier tier) =>
        ((IEnumerable<object[]>)EvalSuite.Named(tier)).Select(row => (string)row[0]);
}