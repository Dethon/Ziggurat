using System.Diagnostics;
using Shouldly;

namespace Tests;

// Work that a test starts but cannot await — a background loop, a fire-and-forget effect, a
// sampled observable — leaves the test with nothing to join on, so it has to watch for the state
// the work produces. Sleeping a fixed span asserts that the work fits inside it, which is a claim
// about the machine rather than about the code: under the parallel suite the span expires first
// and the assertion reads as a behaviour failure. Polling makes the wait an upper bound instead of
// a fixed cost — it returns the moment the state is there, so the common case is faster than the
// sleep it replaces and the slow case still passes.
public static class Eventually
{
    // Long enough that only a genuine failure exhausts it, and never paid when the state arrives.
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _interval = TimeSpan.FromMilliseconds(5);

    public static async Task Until(Func<bool> condition, string because)
    {
        if (await Reached(condition))
        {
            return;
        }

        condition().ShouldBeTrue($"waited {_budget.TotalSeconds:0}s for {because}");
    }

    public static async Task Until(Func<ValueTask<bool>> condition, string because)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _budget)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(_interval);
        }

        (await condition()).ShouldBeTrue($"waited {_budget.TotalSeconds:0}s for {because}");
    }

    // A claim that something does *not* happen cannot be polled — there is no state to wait for,
    // only time to give the wrong behaviour a chance to appear. Tests that pin both halves ("it
    // reaches six and stops there") wait for the reachable half with Until and then settle.
    public static Task Settle() => Task.Delay(TimeSpan.FromMilliseconds(250));

    private static async Task<bool> Reached(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _budget)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(_interval);
        }

        return false;
    }
}