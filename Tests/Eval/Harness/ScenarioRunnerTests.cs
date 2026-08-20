using Shouldly;

namespace Tests.Eval.Harness;

// A real model makes a run stochastic, so a scenario declares how many of how many runs must
// pass. What it must never do is retry until green: half these assertions are negative, and a
// retry past a failure hides exactly the regression the scenario exists to catch.
public class ScenarioRunnerTests
{
    [Fact]
    public async Task ARunThatMeetsItsThreshold_Passes_AndEveryDeclaredRunIsTaken()
    {
        // Every run of a passing scenario is taken, deliberately: stopping at the k-th pass would
        // report a rate over however many runs it happened to take, and the scorecard compares
        // rates across model versions.
        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 3), index =>
            Task.FromResult(new RunReading(index == 1 ? ["a failure"] : [], [])));

        result.Passed.ShouldBeTrue();
        result.Passes.ShouldBe(2);
        result.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task TheRunsOfOneScenario_AreInFlightTogether()
    {
        // Three runs used to be three model turns end to end, and a family's scenarios waited
        // their turn behind them. Nothing about k of N asks for that: the runs are independent,
        // and the only reason to take them one at a time is to stop early (below).
        var arrived = 0;
        var all = new TaskCompletionSource();

        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 3), async _ =>
        {
            if (Interlocked.Increment(ref arrived) == 3)
            {
                all.SetResult();
            }

            // A run that is only reached after its predecessor returned never sees this complete.
            await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return new RunReading([], []);
        });

        result.Attempts.ShouldBe(3);
        result.Passes.ShouldBe(3);
    }

    [Fact]
    public async Task AWidthNarrowerThanTheRunCount_HoldsThatManyAtOnce_AndNoMore()
    {
        var gate = new Lock();
        var inFlight = 0;
        var peak = 0;
        var wave = new TaskCompletionSource();

        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 4) { Width = 2 }, async _ =>
        {
            lock (gate)
            {
                inFlight++;
                peak = Math.Max(peak, inFlight);
                if (inFlight == 2)
                {
                    wave.TrySetResult();
                }
            }

            await wave.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (gate)
            {
                inFlight--;
            }

            return new RunReading([], []);
        });

        peak.ShouldBe(2);
        result.Attempts.ShouldBe(4);
    }

    [Fact]
    public async Task AThresholdThatBecomesUnreachable_StopsEarly_AndReportsWhatItObserved()
    {
        // At a width of one this is the whole run policy: the money a run costs is only saved by
        // not starting it, so a scenario that wants the saving asks for runs one at a time.
        var attempts = 0;

        var result = await ScenarioRunner.RunAsync(new RunPolicy(3, 4) { Width = 1 }, () =>
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
    public async Task ANarrowWidth_StillStopsLaunching_OnceTheThresholdIsUnreachable()
    {
        var launched = 0;

        var result = await ScenarioRunner.RunAsync(new RunPolicy(3, 4) { Width = 2 }, _ =>
        {
            Interlocked.Increment(ref launched);
            return Task.FromResult(new RunReading(["nope"], []));
        });

        // Two failures out of four make three passes impossible. A run already in flight when the
        // second one lands is money spent either way, so what a width buys back is the runs behind
        // it: the fourth is never launched.
        launched.ShouldBe(3);
        result.Attempts.ShouldBe(3);
        result.Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task AClaimExercisedOnlyOnSomeRuns_IsTalliedOverThoseRunsAlone()
    {
        // Delegation is the model's own coin, so a conditional claim's denominator is the runs
        // that produced its material — a rate over all three would count the in-place runs as
        // evidence about a prompt nobody wrote.
        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 3), index =>
            Task.FromResult(index switch
            {
                0 => new RunReading([], ["subagents.prompt-is-self-contained"]),
                1 => new RunReading(["a failure"], ["subagents.prompt-is-self-contained"]),
                _ => new RunReading([], [])
            }));

        var claim = result.Conditionals.ShouldHaveSingleItem();
        claim.Claim.ShouldBe("subagents.prompt-is-self-contained");
        claim.Runs.ShouldBe(2);
        claim.Passes.ShouldBe(1);
    }

    [Fact]
    public async Task TheRateIsOverWhatWasActuallyRun_NeverOverWhatWasDeclared()
    {
        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 2) { Width = 1 }, () =>
            Task.FromResult<IReadOnlyList<string>>(["nope"]));

        result.Attempts.ShouldBe(1);
        result.Rate.ShouldBe(0);
    }

    [Fact]
    public async Task ARunThatThrows_FailsTheScenario_AndItsSiblingsAreStillAwaited()
    {
        // Runs in flight together are runs whose exceptions all have to land somewhere: one left
        // unobserved is a crash on the finalizer thread, in a different test, minutes later.
        var settled = 0;

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            ScenarioRunner.RunAsync(new RunPolicy(2, 3), async index =>
            {
                await Task.Yield();
                Interlocked.Increment(ref settled);
                throw new InvalidOperationException($"run {index} could not arm its timer");
            }));

        settled.ShouldBe(3);
        // The earliest run's, so the same broken stack reports the same message every time.
        thrown.Message.ShouldBe("run 0 could not arm its timer");
    }
}