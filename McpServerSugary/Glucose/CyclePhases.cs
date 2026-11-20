using Domain.DTOs.Sugary;

namespace McpServerSugary.Glucose;

public sealed record CyclePeriod(DateOnly Start, DateOnly? End);

// Days on her calendar, both ends included.
public sealed record DayRange(DateOnly First, DateOnly Last)
{
    public int Days => Last.DayNumber - First.DayNumber + 1;
}

// One finished cycle split into its parts. A part that cannot be told (her period's end not noted, or
// a cycle too short for days between) is null.
public sealed record CycleParts(DateOnly Start, DayRange? Menstrual, DayRange? Follicular, DayRange Luteal);

// Her glucose over one part of her cycles. The shares are of the time with readings, as the Glucose
// tab's are; Low and High are under 70 and over 180 (cumulative, GlucoseStatistics). Every figure is
// null under MinimumCoverage: a few days with readings would be read as her pattern.
public sealed record PhaseFigures(int Days, double Coverage, double? Mean, double? InRange, double? Low, double? High);

public sealed record CyclePhaseFigures(int Cycles, PhaseFigures Menstrual, PhaseFigures Follicular, PhaseFigures Luteal);

// Her glucose in each part of the cycles she has finished: her period, the days after it (follicular)
// and the fourteen before the next start (luteal) — the app's forecast takes ovulation the same
// fourteen days before a start (cycle.ts, LUTEAL_DAYS). Read off the period starts she logged, never a
// forecast. A description of her data, never a dosing rule: what the app says over it is the app's.
public static class CyclePhases
{
    public const int LutealDays = 14;
    public const int MostCycles = 6;
    public const double MinimumCoverage = 0.5;

    // The forecast's rule (cycle.ts, OUTLIER): a cycle half again longer than the median is a period
    // she did not note.
    private const double Outlier = 1.5;

    public static IReadOnlyList<CycleParts> Cycles(IReadOnlyList<CyclePeriod> periods, DateOnly today)
    {
        var sorted = periods.Where(p => p.Start <= today).OrderBy(p => p.Start).ToList();
        var finished = sorted.Zip(sorted.Skip(1), (period, next) => (Period: period, Next: next.Start)).ToList();
        if (finished.Count == 0)
        {
            return [];
        }

        var lengths = finished.Select(c => c.Next.DayNumber - c.Period.Start.DayNumber).Order().ToList();
        var median = lengths.Count % 2 == 1
            ? lengths[lengths.Count / 2]
            : (lengths[(lengths.Count / 2) - 1] + lengths[lengths.Count / 2]) / 2.0;
        return
        [
            .. finished
                .Where(c => c.Next.DayNumber - c.Period.Start.DayNumber <= median * Outlier)
                .TakeLast(MostCycles)
                .Select(c => Parts(c.Period, c.Next))
        ];
    }

    private static CycleParts Parts(CyclePeriod period, DateOnly next)
    {
        var lastDay = next.AddDays(-1);
        var menstrual = period.End is { } end ? new DayRange(period.Start, end < lastDay ? end : lastDay) : null;
        var lutealFirst = next.AddDays(-LutealDays);
        var afterPeriod = (menstrual?.Last ?? period.Start).AddDays(1);
        // Never into her period: a short cycle's fourteen days start after it.
        var luteal = new DayRange(Max(lutealFirst, menstrual is null ? period.Start : afterPeriod), lastDay);
        var follicular = menstrual is not null && luteal.First > afterPeriod
            ? new DayRange(afterPeriod, luteal.First.AddDays(-1))
            : null;
        return new CycleParts(period.Start, menstrual, follicular, luteal);
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    public static CyclePhaseFigures Of(
        IReadOnlyList<CyclePeriod> periods, IEnumerable<SensorReading> readings, TimeZoneInfo zone, DateOnly today,
        int targetLow, int targetHigh)
    {
        var cycles = Cycles(periods, today);
        var all = readings.OrderBy(r => r.At).ToList();
        PhaseFigures Phase(Func<CycleParts, DayRange?> part) =>
            Joined([.. cycles.Select(part).OfType<DayRange>()], all, zone, targetLow, targetHigh);
        return new CyclePhaseFigures(cycles.Count, Phase(c => c.Menstrual), Phase(c => c.Follicular), Phase(c => c.Luteal));
    }

    // Each range figured as the Glucose tab figures a period, then joined by the minutes each covered,
    // so a phase across cycles is weighed exactly as one long period would be.
    private static PhaseFigures Joined(
        IReadOnlyList<DayRange> ranges, IReadOnlyList<SensorReading> readings, TimeZoneInfo zone, int low, int high)
    {
        var figured = ranges
            .Select(range => (From: StartOf(range.First, zone), To: StartOf(range.Last.AddDays(1), zone)))
            .Select(span => (Minutes: (span.To - span.From).TotalMinutes,
                Figures: GlucoseStatistics.Of(readings.Where(r => r.At >= span.From && r.At < span.To), span.From, span.To, low, high)))
            .Select(f => (f.Minutes, Covered: f.Figures.Coverage * f.Minutes, f.Figures))
            .ToList();
        var days = ranges.Sum(range => range.Days);
        var minutes = figured.Sum(f => f.Minutes);
        var covered = figured.Sum(f => f.Covered);
        var coverage = minutes > 0 ? covered / minutes : 0;
        if (coverage < MinimumCoverage || covered <= 0)
        {
            return new PhaseFigures(days, coverage, null, null, null, null);
        }

        double Weighed(Func<GlucoseFigures, double?> figure) =>
            figured.Where(f => f.Covered > 0).Sum(f => figure(f.Figures)!.Value * f.Covered) / covered;

        return new PhaseFigures(
            days, coverage, Weighed(f => f.Mean), Weighed(f => f.InRange), Weighed(f => f.Low), Weighed(f => f.High));
    }

    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}