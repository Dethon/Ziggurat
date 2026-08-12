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
// Timers are tracked by due time because a loop usually has more than one outstanding — a reply
// backstop and a playback tail overlap — and advancing on "some timer appeared" would fire whichever
// came first. Both `Task.Delay(span, clock, ct)` and `task.WaitAsync(span, clock, ct)` arm exactly
// one timer whose due time is that span, so the span the production code passes is the handle the
// test already knows. A zero-length delay completes without arming anything, so there is nothing to
// wait for and nothing to advance.
public sealed class ArmedClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _armed = [];

    public ArmedClock() : this(DateTimeOffset.UtcNow)
    {
    }

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _armed.Add(dueTime);
        }
        return base.CreateTimer(callback, state, dueTime, period);
    }

    // Monotonic, so a caller can wait for growth past a count it took earlier without a live timer
    // being disposed underneath it.
    public int ArmedFor(TimeSpan due)
    {
        lock (_gate)
        {
            return _armed.Count(t => t == due);
        }
    }

    public int ArmedTotal
    {
        get
        {
            lock (_gate)
            {
                return _armed.Count;
            }
        }
    }

    // Waits for the code under test to arm the wait this advance is meant to end, then ends it.
    // `previously` carries the count already armed for that due time when the same wait recurs — a
    // second follow-up window arms a second tail, and without it the first one's timer answers for
    // both.
    public async Task AdvancePastAsync(TimeSpan due, int previously = 0)
    {
        await WaitUntilArmedAsync(due, previously);
        Advance(due);
    }

    // For the other half of the same problem: a test that parks a wait and then does something it
    // expects the parked wait to see. Arming the timeout is the last step of parking, so a timer
    // with that due time is the signal that the code is ready to be interfered with.
    public Task WaitUntilArmedAsync(TimeSpan due, int previously = 0) =>
        Eventually.Until(
            () => ArmedFor(due) > previously, $"the code to arm a {due.TotalMilliseconds:0}ms wait");

    // For a wait whose span the code computes rather than the test choosing it — a playback tail is
    // as long as the audio is. The test cannot name the due time, but it knows it started something
    // that parks exactly once, so "one more timer than there were" identifies it. Take `previously`
    // immediately before the step that arms it, or a timer from earlier in the test answers instead.
    public Task WaitUntilAnyArmedAsync(int previously) =>
        Eventually.Until(() => ArmedTotal > previously, "the code to arm a wait of its own choosing");

    public async Task AdvanceWhenAnyArmedAsync(int previously, TimeSpan by)
    {
        await WaitUntilAnyArmedAsync(previously);
        Advance(by);
    }
}