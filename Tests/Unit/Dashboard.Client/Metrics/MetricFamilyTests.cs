using Dashboard.Client.Metrics;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Metrics;

public sealed class MetricFamilyTests
{
    // Awaiting a refresh means the breakdown reflects the state at or after the call, whichever of
    // the two things the call did: joined a pass that had not applied yet, or started a new one.
    // The run used to end inside one lock acquisition and retire itself inside another, and a caller
    // arriving between the two was told to await a pass that was already over — and then had its
    // request wiped by the retirement that followed.
    [Fact]
    public async Task RefreshAsync_CallersRaceThePassThatIsEnding_EveryCallSeesTheStateItAskedFor()
    {
        var version = 0;
        var applied = 0;
        var family = new MetricFamily(
            "stress",
            MetricChoice.For("groupBy", () => TokenDimension.User, _ => { }),
            metric: null,
            setDateRange: (_, _) => { },
            loadEvents: () => Task.FromResult<Action>(() => { }),
            refreshBreakdown: async () =>
            {
                // Yielding is what puts the end of the pass on a different thread from the caller,
                // which is the only way the handoff can be observed at all.
                await Task.Yield();
                Volatile.Write(ref applied, Volatile.Read(ref version));
            });

        var stale = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            foreach (var _ in Enumerable.Range(0, 500))
            {
                var asked = Interlocked.Increment(ref version);
                await family.RefreshAsync();

                if (Volatile.Read(ref applied) < asked)
                {
                    Interlocked.Increment(ref stale);
                }
            }
        })));

        stale.ShouldBe(0);
    }

    // Two quick time-pill clicks start two loads over different ranges. The thirty-day response is
    // the slower one and used to land last, leaving thirty days of events under a Today header until
    // the next load. Fetching and writing are two steps for exactly this reason: the write from a
    // load another has already superseded is dropped.
    [Fact]
    public async Task LoadEventsAsync_AnOlderLoadAnswersLast_ItsEventsAreNotApplied()
    {
        var gate = new TaskCompletionSource();
        var answering = "thirty days";
        var slowest = true;
        var applied = "";
        var family = FamilyWith(async () =>
        {
            var answer = answering;
            if (slowest)
            {
                slowest = false;
                await gate.Task;
            }

            return () => applied = answer;
        });

        var older = family.LoadEventsAsync();
        answering = "today";
        await family.LoadEventsAsync();
        gate.SetResult();
        await older;

        applied.ShouldBe("today");
    }

    private static MetricFamily FamilyWith(Func<Task<Action>> loadEvents) =>
        new(
            "stale",
            MetricChoice.For("groupBy", () => TokenDimension.User, _ => { }),
            metric: null,
            setDateRange: (_, _) => { },
            loadEvents: loadEvents,
            refreshBreakdown: () => Task.CompletedTask);
}