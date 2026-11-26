namespace Dashboard.Client.Metrics;

public class MetricFamily(
    string name,
    MetricChoice dimension,
    MetricChoice? metric,
    Action<int> setDays,
    Func<Task<Action>> loadEvents,
    Func<Task> refreshBreakdown)
{
    private readonly object _gate = new();
    private Task? _running;
    private bool _dirty;
    private int _loadGeneration;

    public string Name { get; } = name;

    public string PreferenceKeyPrefix { get; } = $"{name}.";

    public MetricChoice Dimension { get; } = dimension;

    // Null for a family with nothing to choose between: errors and schedules have no metric pill,
    // because there is no quantity to pick.
    public MetricChoice? Metric { get; } = metric;

    public void SetDays(int days) => setDays(days);

    // Fetching the events and writing them to the store are two steps, and the second only happens
    // while this load is still the latest one. Two quick time-pill clicks start two loads over
    // different ranges: the thirty-day responses are the slower ones and used to land after the last
    // day's, leaving thirty days of events under a 24h header until the next load. Unlike a breakdown,
    // which the refresh coalescer brings back into line on its own, nothing re-reads an event list.
    public async Task LoadEventsAsync()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        var apply = await loadEvents();

        if (Volatile.Read(ref _loadGeneration) == generation)
        {
            apply();
        }
    }

    // Awaiting this means the breakdown reflects the store state at or after the call. A caller
    // arriving while a run is in flight shares that run and marks it stale, so one further pass
    // serves every caller that arrived during the previous one: a burst landing on one outstanding
    // request costs two requests rather than one per event, and a stream that never lets up keeps
    // one pass in flight rather than one per event. There is no timer and no waiting window, so
    // nothing is slower than a single refresh. Failure throws to everyone awaiting, because the two
    // callers have different policies for it.
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_running is { } running)
            {
                _dirty = true;
                return running;
            }

            var run = RunAsync();
            _running = run.IsCompleted ? null : run;
            return run;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await refreshBreakdown();
            }
            catch
            {
                Retire();
                throw;
            }

            lock (_gate)
            {
                // Ending the loop and retiring the run are one lock acquisition, not two. Between
                // the two, a caller found _running still set, marked the pass stale and was handed a
                // task that had already done its work — and then the retirement cleared the very
                // flag it had just set, so the pass it asked for never happened.
                if (!_dirty)
                {
                    _running = null;
                    return;
                }

                _dirty = false;
            }
        }
    }

    private void Retire()
    {
        lock (_gate)
        {
            _running = null;
            _dirty = false;
        }
    }
}

public sealed class MetricFamily<TStore>(
    TStore store,
    string name,
    MetricChoice dimension,
    MetricChoice? metric,
    Action<int> setDays,
    Func<Task<Action>> loadEvents,
    Func<Task> refreshBreakdown)
    : MetricFamily(name, dimension, metric, setDays, loadEvents, refreshBreakdown)
    where TStore : class
{
    public TStore Store { get; } = store;
}