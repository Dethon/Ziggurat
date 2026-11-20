namespace McpServerSugary.Glucose;

public sealed record Forecast(DateOnly Earliest, DateOnly Latest, DateOnly Likeliest);

// When her next period is expected, from her own recent cycles: the app's forecast (cycle.ts) to the
// day, so the reminder it sends names the day the app shows. Both are pinned to the same cases
// (Sugary.Client/src/cases/forecast.json); a change to one is made to the other.
public static class CycleForecast
{
    // cycle.ts: RECENT_CYCLES and LEAST_SPREAD.
    private const int RecentCycles = 6;
    private const int LeastSpread = 2;

    public static Forecast? Of(IReadOnlyList<CyclePeriod> periods, DateOnly today)
    {
        var (cycles, anchor) = CycleCounting.Of(periods.Where(p => p.Start <= today).Select(p => p.Start));
        var kept = cycles.Where(c => !c.Skipped).TakeLast(RecentCycles).Select(c => c.Days).ToList();
        if (anchor is not { } last || kept.Count == 0)
        {
            return null;
        }

        // Math.round's rounding: half a day up.
        var likeliest = (int)Math.Round(CycleCounting.Median(kept), MidpointRounding.AwayFromZero);
        return new Forecast(
            last.AddDays(Math.Min(kept.Min(), likeliest - LeastSpread)),
            last.AddDays(Math.Max(kept.Max(), likeliest + LeastSpread)),
            last.AddDays(likeliest));
    }
}