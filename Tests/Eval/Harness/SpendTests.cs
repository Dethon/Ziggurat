using Domain.DTOs.Metrics;
using Shouldly;

namespace Tests.Eval.Harness;

// What a run cost, read off the same usage events the deployment's metrics read, so the scorecard
// can say what a pass spent and how much of its prompt the provider served from cache. Before this
// the bill was a number on a dashboard nobody could tie to a scenario.
public class SpendTests
{
    [Fact]
    public void ARecording_SumsTheUsageEventsItIsHanded()
    {
        var recording = new Recording();

        recording.Publish(Usage(cost: 0.010m, input: 9_000, cached: 8_000, output: 100));
        recording.Publish(Usage(cost: 0.012m, input: 9_500, cached: 8_000, output: 300));

        recording.Spend.Total.ShouldBe(new ModelSpend(0.022m, 18_500, 16_000, 400, Requests: 2));
    }

    // Every model the run paid is a model like any other — the agent's, the rubric judge's, Jev's
    // — summed into the total and kept apart by the id its usage named, so a pass can say what
    // each one cost without a field per use.
    [Fact]
    public void ARecording_SplitsTheSpendByTheModelEachUsageNamed()
    {
        var recording = new Recording();

        recording.Publish(Usage(cost: 0.010m, input: 9_000, cached: 8_000, output: 100, model: "z-ai/glm-5"));
        recording.Publish(Usage(cost: 0.00002m, input: 400, cached: null, output: 60, model: "typesafe/jev"));
        recording.Publish(Usage(cost: 0.012m, input: 9_500, cached: 8_000, output: 300, model: "z-ai/glm-5"));

        var spend = recording.Spend;
        spend.ByModel.Keys.ShouldBe(["typesafe/jev", "z-ai/glm-5"]);
        spend.ByModel["z-ai/glm-5"].ShouldBe(new ModelSpend(0.022m, 18_500, 16_000, 400, Requests: 2));
        spend.ByModel["typesafe/jev"].ShouldBe(new ModelSpend(0.00002m, 400, null, 60, Requests: 1));
        spend.Total.ShouldBe(new ModelSpend(0.02202m, 18_900, 16_000, 460, Requests: 3));
    }

    // A judgment's own event is telemetry, not money: the judge's usage event is its bill, as a
    // chat turn's is, and counting the judgment too would charge every judgment twice.
    [Fact]
    public void AJudgmentsOwnEvent_IsNotSpend()
    {
        var recording = new Recording();

        recording.Publish(new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.None, InputTokens = 2200, Cost = 0.0001m, Model = "j" });
        recording.Publish(new ExecScreenEvent { Reach = "contained", Outcome = ExecScreenOutcomes.Ran, InputTokens = 500, Cost = 0.00002m, Model = "j" });

        recording.Spend.ShouldBe(Spend.Nothing);
    }

    [Fact]
    public void AnEventThatIsNotUsage_IsNotSpend()
    {
        var recording = new Recording();

        recording.Publish(new ToolCallEvent { ToolName = "t", Success = true, DurationMs = 1 });

        recording.Spend.ShouldBe(Spend.Nothing);
    }

    [Fact]
    public void CachedTokens_StayUnknown_WhileNoProviderReportedThem()
    {
        // Null is "the provider said nothing", the way the metric event spells it: a zero here
        // would read as a prompt nothing cached, which is a finding rather than an absence.
        var recording = new Recording();
        recording.Publish(Usage(cost: 0.01m, input: 9_000, cached: null, output: 100));

        recording.Spend.CachedInputTokens.ShouldBeNull();
        recording.Spend.CacheShare.ShouldBeNull();
    }

    [Fact]
    public void CachedTokens_AreSummedOverTheRequestsThatReportedThem()
    {
        var spend = Spend.Of(Usage(cost: 0.01m, input: 1_000, cached: null, output: 1))
                    + Spend.Of(Usage(cost: 0.01m, input: 1_000, cached: 600, output: 1));

        spend.CachedInputTokens.ShouldBe(600);
        spend.CacheShare!.Value.ShouldBe(0.3, 0.001);
    }

    [Fact]
    public void TwoSpends_AreEqual_ByWhatEachModelCost()
    {
        var once = Spend.Of("m", new ModelSpend(0.05m, 10_000, 5_000, 200, Requests: 3));
        var again = Spend.Of("m", new ModelSpend(0.05m, 10_000, 5_000, 200, Requests: 3));

        again.ShouldBe(once);
        (once + Spend.Nothing).ShouldBe(once);
        Spend.Of("other", once.Total).ShouldNotBe(once);
    }

    [Fact]
    public async Task TheRunnerSums_TheSpendOfEveryRunItTook()
    {
        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 2), _ =>
            Task.FromResult(new RunReading([], [])
            {
                Spend = Spend.Of("m", new ModelSpend(0.05m, 10_000, 5_000, 200, Requests: 3))
            }));

        result.Spend.ShouldBe(Spend.Of("m", new ModelSpend(0.10m, 20_000, 10_000, 400, Requests: 6)));
    }

    private static TokenUsageEvent Usage(decimal cost, int input, long? cached, int output, string model = "m") => new()
    {
        Sender = "eval",
        Model = model,
        InputTokens = input,
        OutputTokens = output,
        CachedInputTokens = cached,
        Cost = cost
    };
}