using Dashboard.Client.Metrics;

namespace Dashboard.Client.Effects;

public sealed class DataLoadEffect(MetricFamilyTable families, OverviewFigures overview, MetricsHubBinder binder)
{
    // The one trace the swallowed failure leaves behind. The live connection skips catch-up on its
    // first epoch on the premise that this load delivered the same data; a recorded failure is how
    // it knows that premise did not hold.
    public bool LastLoadFailed { get; private set; }

    // Raised after every load has settled, success or failure. The hub can become live while the
    // initial load is still in flight, so the flag alone is not enough: whoever skipped catch-up on
    // its promise needs to hear when the load actually delivers or fails.
    public event Func<Task>? LoadCompleted;

    // The range belongs to the page that asked for it, so only the families that page draws are
    // stamped with it; the rest keep the range their own page chose. Stamping all seven meant a
    // live push or a catch-up for a family whose page had picked thirty days re-read it over
    // whatever the page in front of the user was showing, which is the opposite of what
    // .claude/rules/observability.md promises about catch-up keeping the user's time choices.
    // Everything is still loaded, each family over its own range: whatever a load can put on screen
    // is what an outage can leave stale.
    public async Task LoadAsync(DateOnly from, DateOnly to, IReadOnlyList<MetricFamily> displayed)
    {
        ArgumentNullException.ThrowIfNull(displayed);

        displayed.ToList().ForEach(family => family.SetDateRange(from, to));
        overview.SetDateRange(from, to);
        await ReloadAsync();
    }

    // Everything read again over the ranges each family and the summary already hold: what a change
    // of household (HouseholdFilter) asks, since the pages' time choices stay where they were.
    public async Task ReloadAsync()
    {
        var requests = new Func<Task>[]
        {
            overview.LoadSummaryAsync,
            overview.LoadHealthAsync,
        }.Concat(families.All.SelectMany(family => new Func<Task>[]
        {
            family.LoadEventsAsync,
            family.RefreshAsync,
        }));

        // Held for the same reason catch-up holds: this walk replaces the same event lists a push
        // writes into, and without the hold a push landing mid-load is erased when the load's
        // older response lands after it.
        binder.HoldPushes();
        bool[] outcomes;
        try
        {
            outcomes = await Task.WhenAll(requests.Select(SettleAsync));
        }
        finally
        {
            await binder.ReleaseHeldPushesAsync();
        }

        LastLoadFailed = outcomes.Contains(false);

        if (LoadCompleted is { } completed)
        {
            await completed();
        }
    }

    // Each request settles on its own, because one endpoint that fails must not take the panels that
    // did answer off the screen: the KPI row and the health grid used to go blank whenever any of
    // the nineteen breakdown requests failed. A refresh can also hand back a run started by a live
    // push and already failing, which is a failure this load never issued and still has to survive.
    // The page-load path swallows the reason, as it always has — connection status is the live
    // connection's to publish, and a failed request is not an outage.
    private static async Task<bool> SettleAsync(Func<Task> request)
    {
        try
        {
            await request();
            return true;
        }
        catch
        {
            return false;
        }
    }
}