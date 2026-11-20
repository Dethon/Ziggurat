namespace McpServerSugary.Glucose;

public sealed record Forecast(DateOnly Earliest, DateOnly Latest, DateOnly Likeliest);

// When her next period is expected, from her own recent cycles: the app's forecast (cycle.ts) to the
// day, so the reminder it sends names the day the app shows. Both are pinned to the same cases
// (Sugary.Client/src/cases/forecast.json); a change to one is made to the other.
public static class CycleForecast
{
    // cycle.ts: RECENT_CYCLES and OUTLIER.
    private const int RecentCycles = 6;
    private const double Outlier = 1.5;

    public static Forecast? Of(IReadOnlyList<CyclePeriod> periods, DateOnly today)
    {
        var starts = periods.Where(p => p.Start <= today).Select(p => p.Start).Order().ToList();
        if (starts.Count < 2)
        {
            return null;
        }

        var recent = starts.Zip(starts.Skip(1), (a, b) => b.DayNumber - a.DayNumber).TakeLast(RecentCycles).ToList();
        var sorted = recent.Order().ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2.0;
        var kept = recent.Where(days => days <= median * Outlier).ToList();
        var last = starts[^1];
        // Math.round's rounding: half a day up.
        var mean = (int)Math.Round(kept.Average(), MidpointRounding.AwayFromZero);
        return new Forecast(last.AddDays(kept.Min()), last.AddDays(kept.Max()), last.AddDays(mean));
    }
}