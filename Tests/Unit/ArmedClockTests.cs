using Shouldly;

namespace Tests.Unit;

public class ArmedClockTests
{
    // A timer the clock reports armed is one an advance fires. The clock used to count a timer
    // before the fake provider held it, so an advance landing between the two moved time under a
    // timer not yet registered, which was then due a whole span after the advance and never fired —
    // a wait that timed out at a minute in the full suite, once in many runs.
    [Fact]
    public async Task ATimerReportedArmed_IsFiredByTheAdvance()
    {
        var stuck = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, 20_000),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 },
            async (_, _) =>
            {
                var clock = new ArmedClock();
                var delay = Task.Run(() => Task.Delay(TimeSpan.FromSeconds(1), clock));

                await clock.AdvancePastAsync(TimeSpan.FromSeconds(1));

                if (await Task.WhenAny(delay, Task.Delay(TimeSpan.FromSeconds(2))) != delay)
                {
                    Interlocked.Increment(ref stuck);
                }
            });

        stuck.ShouldBe(0);
    }
}