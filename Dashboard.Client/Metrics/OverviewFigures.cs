using Dashboard.Client.Services;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;

namespace Dashboard.Client.Metrics;

// The two reads that belong to no metric family: the summary totals behind the Overview KPI row and
// the service health roster. They sit beside the family table because the page load and the catch-up
// walk both want them, and neither should own the mapping from response to store.
public sealed class OverviewFigures(
    MetricsApiService api,
    MetricsStore metricsStore,
    HealthStore healthStore)
{
    private (DateOnly From, DateOnly To)? _range;
    private int _summaryGeneration;

    public void SetDateRange(DateOnly from, DateOnly to) => _range = (from, to);

    // The summary is per range and catch-up is never told one, so it re-reads over the range the
    // last page load set, exactly as the family walk reads its range from the families. Before any
    // load has set one there is no summary on screen to correct, so there is nothing to read.
    //
    // Only the latest read writes, for the reason MetricFamily's load only lets the latest one
    // write: two quick time-pill clicks issue two summary requests, and the thirty-day one is the
    // slower of the two, so the KPI row used to settle on totals from a range the header no longer
    // shows. Deriving the totals from the event lists takes the same stamp, so a read still in
    // flight cannot land on top of a catch-up's derivation either.
    public async Task LoadSummaryAsync()
    {
        if (_range is not { } range)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _summaryGeneration);
        var summary = await api.GetSummaryAsync(range.From, range.To);
        if (summary is null || Volatile.Read(ref _summaryGeneration) != generation)
        {
            return;
        }

        metricsStore.UpdateSummary(new MetricsState
        {
            InputTokens = summary.InputTokens,
            OutputTokens = summary.OutputTokens,
            Cost = summary.Cost,
            ToolCalls = summary.ToolCalls,
            ToolErrors = summary.ToolErrors,
            TotalRecalls = summary.TotalRecalls,
            TotalExtractions = summary.TotalExtractions,
            TotalDreamings = summary.TotalDreamings,
            MemoriesStored = summary.MemoriesStored,
            MemoriesMerged = summary.MemoriesMerged,
            MemoriesDecayed = summary.MemoriesDecayed,
        });
    }

    // The same totals, added up from the event lists a catch-up has just reloaded rather than read
    // back from the summary endpoint. Catch-up holds pushes and then asks each one whether the
    // events snapshot already delivered it; a summary read on its own clock answered a different
    // instant, so an event written between the two responses was counted twice (events snapshot
    // older, the held push applied on top of a total that already had it) or lost whole (summary
    // older, the held push dropped against a total that never had it). Derived here, the totals and
    // the dedupe answer are the same snapshot by construction. A family whose reload failed keeps
    // the list it had, and that is also the list its pushes are deduped against, so the KPI row
    // still agrees with what the charts show.
    public void DeriveSummaryFromEvents(MetricFamilyTable families)
    {
        ArgumentNullException.ThrowIfNull(families);

        Interlocked.Increment(ref _summaryGeneration);

        var tokens = families.Tokens.Store.State.Events;
        var tools = families.Tools.Store.State.Events;
        var memory = families.Memory.Store.State;

        metricsStore.UpdateSummary(new MetricsState
        {
            InputTokens = tokens.Sum(evt => (long)evt.InputTokens),
            OutputTokens = tokens.Sum(evt => (long)evt.OutputTokens),
            Cost = tokens.Sum(evt => evt.Cost),
            ToolCalls = tools.Count,
            ToolErrors = tools.Count(evt => !evt.Success),
            TotalRecalls = memory.RecallEvents.Count,
            TotalExtractions = memory.ExtractionEvents.Count,
            TotalDreamings = memory.DreamingEvents.Count,
            MemoriesStored = memory.ExtractionEvents.Sum(evt => (long)evt.StoredCount),
            MemoriesMerged = memory.DreamingEvents.Sum(evt => (long)evt.MergedCount),
            MemoriesDecayed = memory.DreamingEvents.Sum(evt => (long)evt.DecayedCount),
        });
    }

    public async Task LoadHealthAsync()
    {
        var health = await api.GetHealthAsync();
        if (health is null)
        {
            return;
        }

        healthStore.UpdateHealth(health
            .Select(h => new ServiceHealth(h.Service, h.IsHealthy, h.LastSeen, h.Household))
            .ToList());
    }
}