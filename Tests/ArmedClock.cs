using Microsoft.Extensions.Time.Testing;

namespace Tests;

// Advancing a fake clock is a claim about ordering: the code under test must already have asked for
// the timer the advance is meant to fire. Nothing in FakeTimeProvider enforces that. An advance past
// a timer that does not exist yet fires nothing, the code arms it a moment later against a clock
// that has already moved, and the test then waits for an effect that can no longer arrive — so the
// failure surfaces as a timeout that never fired rather than as the ordering that did not hold.
//
// Tests bridged that gap by sleeping first, which makes the ordering likely instead of certain, and
// the suite running at full width is where likely runs out. A timer announces itself when it is
// created, so waiting for one is an observation of the precondition rather than a guess at it.
//
// Timers carry their due time because a loop usually has more than one outstanding — a reply
// backstop and a playback tail overlap — and advancing on "some timer appeared" would fire whichever
// came first. Both `Task.Delay(span, clock, ct)` and `task.WaitAsync(span, clock, ct)` arm exactly
// one timer whose due time is that span, so the span the production code passes is the handle the
// test already holds. A zero-length delay completes without arming anything, so a wait of no length
// is not one of these and needs no advance.
public sealed class ArmedClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly Lock _gate = new();

    // Two counts, because a test asks one of two different questions. `_armed` is monotonic and
    // answers "has this ever been armed", which suits a wait that happens once. `_live` rises and
    // falls with the waits actually outstanding, which is what a wait that recurs needs — Task.Delay
    // and Task.WaitAsync each dispose their timer when they end, so a second identical wait later in
    // the same test is distinguishable from the first one having already fired.
    private readonly List<TimeSpan> _armed = [];
    private readonly List<TimeSpan> _live = [];

    public ArmedClock() : this(DateTimeOffset.UtcNow)
    {
    }

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _armed.Add(dueTime);
            _live.Add(dueTime);
        }

        return new Tracked(base.CreateTimer(callback, state, dueTime, period), this, dueTime);
    }

    // Waits for the code under test to arm the wait this advance is meant to end, then ends it.
    // `previously` carries the count already armed for that due time when the same wait recurs, so
    // the first one's timer cannot answer for the second.
    public async Task AdvancePastAsync(TimeSpan due, int previously = 0)
    {
        await WaitUntilArmedAsync(due, previously);
        Advance(due);
    }

    // The other half of the same problem: a test that parks a wait and then does something it
    // expects the parked wait to see. Arming the timeout is the last step of parking, so a timer
    // with that due time is the signal that the code is ready to be interfered with.
    public Task WaitUntilArmedAsync(TimeSpan due, int previously = 0) =>
        Eventually.Until(
            () => ArmedFor(due) > previously, $"the code to arm a {due.TotalMilliseconds:0}ms wait");

    // The same question asked as "one is outstanding right now", for a wait a test settles more than
    // once. It needs no baseline and cannot be answered by a timer that has already fired.
    public Task WaitForLiveAsync(TimeSpan due) =>
        Eventually.Until(
            () => Live(t => t == due), $"a {due.TotalMilliseconds:0}ms wait to be outstanding");

    // For a wait whose span the code computes rather than the test choosing it — a playback tail is
    // as long as its audio, which the test has no name for.
    public Task WaitForAnyLiveAsync() =>
        Eventually.Until(() => Live(_ => true), "a wait of the code's own choosing to be outstanding");

    public async Task AdvancePastLiveAsync(TimeSpan due, TimeSpan by)
    {
        await WaitForLiveAsync(due);
        Advance(by);
    }

    private int ArmedFor(TimeSpan due)
    {
        lock (_gate)
        {
            return _armed.Count(t => t == due);
        }
    }

    private bool Live(Func<TimeSpan, bool> match)
    {
        lock (_gate)
        {
            return _live.Any(match);
        }
    }

    private sealed class Tracked(ITimer inner, ArmedClock clock, TimeSpan due) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            Retire();
            inner.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            Retire();
            await inner.DisposeAsync();
        }

        private void Retire()
        {
            // Disposal is not promised to happen once, and a second pass would retire a live timer
            // belonging to whoever armed the same span next.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (clock._gate)
            {
                clock._live.Remove(due);
            }
        }
    }
}