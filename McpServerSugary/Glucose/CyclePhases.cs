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

// Her glucose over one part of her cycles, from the cycles whose part had readings for at least
// MinimumCoverage of its time (a few days would be read as her pattern); Days and Coverage are of those.
// The shares are of the time with readings, as the Glucose tab's are; Low and High are under 70 and over
// 180 (cumulative, GlucoseStatistics). Every figure null when no cycle's part was read.
public sealed record PhaseFigures(int Days, double Coverage, double? Mean, double? InRange, double? Low, double? High);

// Cycles: how many had any part read.
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
        var figured = cycles
            .Select(cycle => (
                Menstrual: Figure(cycle.Menstrual, all, zone, targetLow, targetHigh),
                Follicular: Figure(cycle.Follicular, all, zone, targetLow, targetHigh),
                Luteal: Figure(cycle.Luteal, all, zone, targetLow, targetHigh)))
            .ToList();
        return new CyclePhaseFigures(
            figured.Count(c => new[] { c.Menstrual, c.Follicular, c.Luteal }.Any(r => r is { Read: true })),
            Joined([.. figured.Select(c => c.Menstrual).OfType<Range>()]),
            Joined([.. figured.Select(c => c.Follicular).OfType<Range>()]),
            Joined([.. figured.Select(c => c.Luteal).OfType<Range>()]));
    }

    // One cycle's part, figured as the Glucose tab figures a period. Read when its own time with
    // readings reaches the floor: a sensor worn through one cycle of six still reads that one.
    private sealed record Range(int Days, double Minutes, double Covered, GlucoseFigures Figures)
    {
        public bool Read => Minutes > 0 && Covered / Minutes >= MinimumCoverage && Covered > 0;
    }

    private static Range? Figure(DayRange? days, IReadOnlyList<SensorReading> readings, TimeZoneInfo zone, int low, int high)
    {
        if (days is null)
        {
            return null;
        }

        var (from, to) = (StartOf(days.First, zone), StartOf(days.Last.AddDays(1), zone));
        var minutes = (to - from).TotalMinutes;
        var figures = GlucoseStatistics.Of(readings.Where(r => r.At >= from && r.At < to), from, to, low, high);
        return new Range(days.Days, minutes, figures.Coverage * minutes, figures);
    }

    // The parts read, joined by the minutes each covered, so a phase across cycles is weighed exactly
    // as one long period would be. None read: its days and coverage over every part, and no figures.
    private static PhaseFigures Joined(IReadOnlyList<Range> ranges)
    {
        var read = ranges.Where(r => r.Read).ToList();
        if (read.Count == 0)
        {
            var minutes = ranges.Sum(r => r.Minutes);
            return new PhaseFigures(ranges.Sum(r => r.Days), minutes > 0 ? ranges.Sum(r => r.Covered) / minutes : 0, null, null, null, null);
        }

        var covered = read.Sum(r => r.Covered);
        double Weighed(Func<GlucoseFigures, double?> figure) => read.Sum(r => figure(r.Figures)!.Value * r.Covered) / covered;

        return new PhaseFigures(
            read.Sum(r => r.Days), covered / read.Sum(r => r.Minutes),
            Weighed(f => f.Mean), Weighed(f => f.InRange), Weighed(f => f.Low), Weighed(f => f.High));
    }

    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}