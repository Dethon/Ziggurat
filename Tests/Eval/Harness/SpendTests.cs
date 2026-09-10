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

        recording.Spend.ShouldBe(new Spend(0.022m, 18_500, 16_000, 400, Requests: 2));
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
    public async Task TheRunnerSums_TheSpendOfEveryRunItTook()
    {
        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 2), _ =>
            Task.FromResult(new RunReading([], [])
            {
                Spend = new Spend(0.05m, 10_000, 5_000, 200, Requests: 3)
            }));

        result.Spend.ShouldBe(new Spend(0.10m, 20_000, 10_000, 400, Requests: 6));
    }

    private static TokenUsageEvent Usage(decimal cost, int input, long? cached, int output) => new()
    {
        Sender = "eval",
        Model = "m",
        InputTokens = input,
        OutputTokens = output,
        CachedInputTokens = cached,
        Cost = cost
    };
}