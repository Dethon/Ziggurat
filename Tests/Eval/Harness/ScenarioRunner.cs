namespace Tests.Eval.Harness;

// k of N, with no retry-until-green anywhere in it. A run that cannot reach its threshold stops
// where it is and reports the honest partial rate: spending the remaining runs would cost money
// to learn nothing, and reporting a rate over runs nobody took would be a fabrication.
public static class ScenarioRunner
{
    public static async Task<ScenarioResult> RunAsync(
        RunPolicy policy, Func<Task<IReadOnlyList<string>>> run)
    {
        var passes = 0;
        var attempts = 0;
        var failures = new List<string>();

        while (attempts < policy.N)
        {
            attempts++;
            var result = await run();
            if (result.Count == 0)
            {
                passes++;
            }
            else
            {
                failures.AddRange(result);
            }

            // Unreachable, not merely behind: with this many runs left, even passing all of them
            // cannot reach k.
            if (passes + (policy.N - attempts) < policy.K)
            {
                break;
            }
        }

        return new ScenarioResult(passes >= policy.K, passes, attempts, failures);
    }
}

public sealed record ScenarioResult(
    bool Passed, int Passes, int Attempts, IReadOnlyList<string> Failures)
{
    // Over what was actually run. A scenario that stopped early has a rate over the runs it took,
    // and saying so is the difference between a partial result and an invented one.
    public double Rate => Attempts == 0 ? 0 : (double)Passes / Attempts;
}