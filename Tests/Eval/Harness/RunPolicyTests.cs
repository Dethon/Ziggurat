using Shouldly;

namespace Tests.Eval.Harness;

// A real model makes a run stochastic, so a scenario declares how many of how many runs must
// pass. What it must never do is retry until green: half these assertions are negative, and a
// retry past a failure hides exactly the regression the scenario exists to catch.
public class RunPolicyTests
{
    [Fact]
    public async Task ARunThatMeetsItsThresholdPasses_AndEveryDeclaredRunIsTaken()
    {
        // Every run of a passing scenario is taken, deliberately: stopping at the k-th pass would
        // report a rate over however many runs it happened to take, and the scorecard compares
        // rates across model versions.
        var attempts = 0;

        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 3), () =>
        {
            attempts++;
            return Task.FromResult<IReadOnlyList<string>>(attempts == 2 ? ["a failure"] : []);
        });

        result.Passed.ShouldBeTrue();
        result.Passes.ShouldBe(2);
        result.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task AThresholdThatBecomesUnreachable_StopsEarly_AndReportsWhatItObserved()
    {
        var attempts = 0;

        var result = await ScenarioRunner.RunAsync(new RunPolicy(3, 4), () =>
        {
            attempts++;
            return Task.FromResult<IReadOnlyList<string>>(["wrote to /schedules"]);
        });

        // Two failures out of four make three passes impossible; the remaining runs are not spent.
        attempts.ShouldBe(2);
        result.Passed.ShouldBeFalse();
        result.Passes.ShouldBe(0);
        result.Attempts.ShouldBe(2);
        result.Failures.ShouldContain("wrote to /schedules");
    }

    [Fact]
    public async Task TheRateIsOverWhatWasActuallyRun_NeverOverWhatWasDeclared()
    {
        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 2), () =>
            Task.FromResult<IReadOnlyList<string>>(["nope"]));

        result.Attempts.ShouldBe(1);
        result.Rate.ShouldBe(0);
    }
}