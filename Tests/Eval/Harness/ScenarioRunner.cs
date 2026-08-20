using System.Runtime.ExceptionServices;

namespace Tests.Eval.Harness;

// k of N, with no retry-until-green anywhere in it. The runs of one scenario go out together up
// to the policy's width, because they are independent of each other and only their number is
// declared; what stays serial is the decision to launch, so a threshold that has become
// unreachable still stops the runs nobody has started yet.
public static class ScenarioRunner
{
    public static Task<ScenarioResult> RunAsync(
        RunPolicy policy, Func<Task<IReadOnlyList<string>>> run) =>
        RunAsync(policy, async _ => new RunReading(await run(), []));

    public static async Task<ScenarioResult> RunAsync(
        RunPolicy policy, Func<int, Task<RunReading>> run)
    {
        // Kept by the run's own number rather than by arrival, so which run explains a failure —
        // and which failures a scorecard reads — does not depend on which finished first.
        var readings = new RunReading?[policy.N];
        var faults = new Exception?[policy.N];
        var running = new List<Task<int>>();
        var width = Math.Max(1, policy.Width);
        var launched = 0;
        var completed = 0;
        var passes = 0;
        var faulted = false;

        // A loop rather than LINQ: what may be launched depends on what has already come back.
        while (true)
        {
            while (launched < policy.N
                   && running.Count < width
                   && !faulted
                   // Unreachable, not merely behind: even if everything still owed came back
                   // green, it could not reach k. The runs already in flight are counted as
                   // green here, because they are owed and nothing can be learned by guessing.
                   && passes + (policy.N - completed) >= policy.K)
            {
                running.Add(attempt(launched++));
            }

            if (running.Count == 0)
            {
                break;
            }

            var finished = await Task.WhenAny(running);
            running.Remove(finished);
            completed++;

            var index = await finished;
            if (faults[index] is not null)
            {
                faulted = true;
            }
            else if (readings[index]!.Failures.Count == 0)
            {
                passes++;
            }
        }

        // Every launched run has been awaited by now, so a second failure cannot come back
        // unobserved and land on the finalizer thread inside somebody else's test.
        if (faults.FirstOrDefault(fault => fault is not null) is { } first)
        {
            ExceptionDispatchInfo.Capture(first).Throw();
        }

        var taken = readings.OfType<RunReading>().ToList();

        return new ScenarioResult(
            passes >= policy.K, passes, completed,
            [.. taken.SelectMany(reading => reading.Failures)])
        {
            Conditionals = Tallied(taken)
        };

        // The run's number travels with it so its reading lands in its own slot; the exception it
        // failed with is carried rather than thrown, so the loop can drain its siblings first.
        async Task<int> attempt(int index)
        {
            try
            {
                readings[index] = await run(index);
            }
            catch (Exception e)
            {
                faults[index] = e;
            }

            return index;
        }
    }

    // A conditional claim's denominator is the runs that produced its material, so the tally is
    // per claim rather than per scenario: the runs that did the work in place are not evidence
    // about a prompt nobody wrote.
    private static IReadOnlyList<ClaimOutcome> Tallied(IReadOnlyList<RunReading> readings) =>
        readings
            .SelectMany(reading => reading.Exercised
                .Select(claim => (Claim: claim, Passed: reading.Failures.Count == 0)))
            .GroupBy(o => o.Claim)
            .Select(group => new ClaimOutcome(
                group.Key, group.Count(o => o.Passed), group.Count()))
            .ToList();
}

// What one run reported: everything that failed, and the conditional claims whose material the
// run actually produced.
public sealed record RunReading(IReadOnlyList<string> Failures, IReadOnlyList<string> Exercised);

public sealed record ScenarioResult(
    bool Passed, int Passes, int Attempts, IReadOnlyList<string> Failures)
{
    // Over what was actually run. A scenario that stopped early has a rate over the runs it took,
    // and saying so is the difference between a partial result and an invented one.
    public double Rate => Attempts == 0 ? 0 : (double)Passes / Attempts;

    // Conditional claims, each over the runs that exercised it rather than over Attempts.
    public IReadOnlyList<ClaimOutcome> Conditionals { get; init; } = [];
}