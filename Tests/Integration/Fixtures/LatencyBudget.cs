namespace Tests.Integration.Fixtures;

// A wall-clock budget, read on a machine that is doing twenty other things.
//
// The browser tests that assert on milliseconds used to run last, as the tail of one serial chain,
// and were read once. They run beside the E2E suite now, which is what took them off the critical
// path — and a single reading taken while twenty-four threads and a dozen containers are competing
// for the host is as much a measurement of the host as of the code. A 700ms budget missed at 1038ms
// once in ten runs, on a path that had not changed.
//
// So the reading is the quickest of a few, which is the one least polluted by whatever else the
// machine was doing. It is not a retry: every attempt still asserts its own correctness, and code
// that genuinely regressed past the budget is over it on all three.
internal static class LatencyBudget
{
    private const int Attempts = 3;

    internal static async Task<long> FastestAsync(Func<Task<long>> attempt)
    {
        var fastest = long.MaxValue;
        foreach (var _ in Enumerable.Range(0, Attempts))
        {
            fastest = Math.Min(fastest, await attempt());
        }

        return fastest;
    }
}