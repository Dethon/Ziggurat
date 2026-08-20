namespace Domain.Monitor;

// How long to wait before starting the monitor again. The loop restarts it on every ending —
// a fault, and equally a stream that simply completed — so with no wait at all a dependency that
// refuses instantly becomes a hot loop that refuses thousands of times a second, buries the one
// log line that says why in its own repetitions, and hammers whatever is already struggling.
public sealed record MonitorRestartPolicy
{
    // Short enough that an ordinary blip costs a person nothing.
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    // Long enough that a dependency down for an hour is retried about thirty times rather than
    // three million, and short enough that nobody waits for the recovery once it comes back.
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(2);

    // How long a run has to last to count as recovery. Anything shorter is the same failure
    // continuing, whatever the exception said.
    public TimeSpan HealthyRun { get; init; } = TimeSpan.FromMinutes(2);
}

// The doubling, the ceiling, the jitter and the reset, with nothing else in it — no clock, no
// logging, no metrics. `NextDelay` is a pure function of the policy, the run that just ended and
// the random draw, which is why every number in it is assertable.
public sealed class MonitorRestartSchedule(MonitorRestartPolicy policy, Func<double>? jitter = null)
{
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    // Doubling stops here whatever the ceiling is: 2^30 of anything is already past every
    // conceivable MaxDelay, and shifting further would overflow rather than saturate.
    private const int MaxDoublings = 30;

    private int _attempt;

    // Consecutive failures behind the current delay. Read for the log line and the metric, so a
    // person seeing a two-minute gap can tell a long outage from a fresh one.
    public int Attempt => _attempt;

    public TimeSpan NextDelay(TimeSpan ranFor)
    {
        // Reset on recovery, and recovery is a run that lasted rather than one that started. A
        // monitor that dies after two seconds ten times running is one failure, not ten first
        // failures, and resetting on each of them would keep the retry rate at its maximum for as
        // long as the outage lasted — which is exactly the loop this policy exists to stop.
        if (ranFor >= policy.HealthyRun)
        {
            _attempt = 0;
        }

        var doubled = policy.InitialDelay * Math.Pow(2, Math.Min(_attempt, MaxDoublings));
        _attempt++;

        return Jittered(doubled > policy.MaxDelay ? policy.MaxDelay : doubled);
    }

    // Equal jitter: half the interval always, plus a random share of the rest. Full jitter would
    // let a delay come out at nearly zero, which is the hot loop again for one unlucky draw; no
    // jitter at all lines up every agent restarting against one dependency onto the same second.
    private TimeSpan Jittered(TimeSpan delay) => (delay / 2) + (delay / 2 * _jitter());
}