using System.Diagnostics;

namespace Tests.E2E.Fixtures;

// A fixture that ran out of budget mid-startup surfaced as a bare "the operation was canceled",
// which named neither the fixture nor what it was waiting on. Since the two fixtures initialise
// concurrently and queue behind each other on the shared base-sdk build, "which phase stalled,
// and how long did it have" is exactly the question a timeout needs to answer.
internal static class E2EPhase
{
    internal static async Task RunAsync(
        string fixtureName,
        string phase,
        TimeSpan budget,
        Func<CancellationToken, Task> body)
    {
        using var cts = new CancellationTokenSource(budget);
        var elapsed = Stopwatch.StartNew();
        try
        {
            await body(cts.Token);
            Console.WriteLine($"[e2e] {fixtureName}: {phase} finished in {elapsed.Elapsed.TotalSeconds:0.0}s");
            E2ETrace.Write($"phase:{phase}", elapsed.Elapsed.TotalSeconds, fixtureName);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{fixtureName}: '{phase}' did not finish within its {budget.TotalMinutes:0.#} minute budget "
                + $"(gave up after {elapsed.Elapsed.TotalSeconds:0}s). Run with the other E2E collection "
                + "filtered out to see whether it was queued behind that fixture's image builds.");
        }
    }
}