namespace Tests.Eval.Harness;

// k of N, with no retry-until-green anywhere in it. A run that cannot reach its threshold stops
// where it is and reports the honest partial rate: spending the remaining runs would cost money
// to learn nothing, and reporting a rate over runs nobody took would be a fabrication.
public static class ScenarioRunner
{
    public static Task<ScenarioResult> RunAsync(
        RunPolicy policy, Func<Task<IReadOnlyList<string>>> run) =>
        RunAsync(policy, async () => new RunReading(await run(), []));

    public static async Task<ScenarioResult> RunAsync(
        RunPolicy policy, Func<Task<RunReading>> run)
    {
        var passes = 0;
        var attempts = 0;
        var failures = new List<string>();
        var readings = new List<RunReading>();

        while (attempts < policy.N)
        {
            attempts++;
            var reading = await run();
            readings.Add(reading);
            if (reading.Failures.Count == 0)
            {
                passes++;
            }
            else
            {
                failures.AddRange(reading.Failures);
            }

            // Unreachable, not merely behind: with this many runs left, even passing all of them
            // cannot reach k.
            if (passes + (policy.N - attempts) < policy.K)
            {
                break;
            }
        }

        return new ScenarioResult(passes >= policy.K, passes, attempts, failures)
        {
            Conditionals = Tallied(readings)
        };
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