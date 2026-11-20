using Domain.DTOs.Sugary;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// Her glucose in each part of her cycle, read off the cycles she has finished: her period, the days
// after it (follicular) and the fourteen before the next (luteal), the forecast's same rule. Figures are
// GlucoseStatistics' over each day range, joined by the minutes each covered.
public class CyclePhasesTests
{
    private static readonly TimeZoneInfo _utc = TimeZoneInfo.Utc;
    private static readonly DateOnly _today = new(2026, 11, 20);

    // A period not noted between June and August: one cycle of 56 days.
    private static readonly string[] _gapStarts = ["2026-05-19", "2026-06-16", "2026-08-11", "2026-09-09"];

    private static DateOnly D(string day) => DateOnly.Parse(day);

    private static CyclePeriod P(string start, string? end) => new(D(start), end is null ? null : D(end));

    // A reading every five minutes over the days given, at one value.
    private static IEnumerable<SensorReading> Flat(string first, string last, double mgPerDl)
    {
        for (var at = new DateTimeOffset(D(first).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
             at < new DateTimeOffset(D(last).AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
             at = at.AddMinutes(5))
        {
            yield return new SensorReading(at, mgPerDl, GlucoseTrend.Unknown);
        }
    }

    [Fact]
    public void ACycle_IsHerPeriod_TheDaysAfter_AndTheFourteenBeforeTheNext()
    {
        var cycles = CyclePhases.Cycles([P("2026-09-01", "2026-09-05"), P("2026-09-29", null)], _today);

        var cycle = cycles.ShouldHaveSingleItem();
        cycle.Menstrual.ShouldBe(new DayRange(D("2026-09-01"), D("2026-09-05")));
        cycle.Follicular.ShouldBe(new DayRange(D("2026-09-06"), D("2026-09-14")));
        cycle.Luteal.ShouldBe(new DayRange(D("2026-09-15"), D("2026-09-28")));
    }

    [Fact]
    public void NoFinishedCycle_IsNoCycle()
    {
        CyclePhases.Cycles([], _today).ShouldBeEmpty();
        CyclePhases.Cycles([P("2026-11-10", null)], _today).ShouldBeEmpty();
    }

    // Without her period's end its days and the ones after cannot be told apart; the luteal days still can.
    [Fact]
    public void APeriodWhoseEndWasNotNoted_GivesOnlyItsLutealDays()
    {
        var cycle = CyclePhases.Cycles([P("2026-09-01", null), P("2026-09-29", "2026-10-03")], _today).Single();

        cycle.Menstrual.ShouldBeNull();
        cycle.Follicular.ShouldBeNull();
        cycle.Luteal.ShouldBe(new DayRange(D("2026-09-15"), D("2026-09-28")));
    }

    // A short cycle's fourteen days would reach into her period: the luteal days start after it.
    [Fact]
    public void AShortCycle_HasNoFollicularDays_AndItsLutealDaysStartAfterHerPeriod()
    {
        var cycle = CyclePhases.Cycles([P("2026-09-01", "2026-09-06"), P("2026-09-19", null)], _today).Single();

        cycle.Follicular.ShouldBeNull();
        cycle.Luteal.ShouldBe(new DayRange(D("2026-09-07"), D("2026-09-18")));
    }

    // The forecast's rule: a cycle half again longer than the median is a period she did not note.
    [Fact]
    public void ACycleHalfAgainLongerThanTheMedian_IsLeftOut()
    {
        var periods = _gapStarts
            .Select(start => P(start, D(start).AddDays(4).ToString("yyyy-MM-dd")))
            .ToList();

        CyclePhases.Cycles(periods, _today).Select(c => c.Start).ShouldBe([D("2026-05-19"), D("2026-08-11")]);
    }

    // The forecast's rule: spotting noted as a period is no start, so the cycle around it is read whole.
    [Fact]
    public void ASpottingStart_IsNoCycle_TheCycleAroundItIsReadWhole()
    {
        var periods = new[] { "2026-05-01", "2026-05-29", "2026-06-26", "2026-07-10", "2026-07-24", "2026-08-21" }
            .Select(start => P(start, D(start).AddDays(4).ToString("yyyy-MM-dd")))
            .ToList();

        var cycles = CyclePhases.Cycles(periods, _today);

        cycles.Select(c => c.Start).ShouldBe([D("2026-05-01"), D("2026-05-29"), D("2026-06-26"), D("2026-07-24")]);
        cycles[2].Luteal.ShouldBe(new DayRange(D("2026-07-10"), D("2026-07-23")));
    }

    [Fact]
    public void OnlyHerLastSixCycles_AreRead()
    {
        var periods = Enumerable.Range(0, 9)
            .Select(n => D("2026-01-01").AddDays(28 * n))
            .Select(start => new CyclePeriod(start, start.AddDays(4)))
            .ToList();

        CyclePhases.Cycles(periods, _today).Select(c => c.Start).ShouldBe(periods.Skip(2).Take(6).Select(p => p.Start));
    }

    [Fact]
    public void EachPhase_IsFiguredFromHerReadingsInIt()
    {
        var periods = new[] { P("2026-09-01", "2026-09-05"), P("2026-09-29", "2026-10-03") };
        var readings = Flat("2026-09-01", "2026-09-05", 150)
            .Concat(Flat("2026-09-06", "2026-09-14", 110))
            .Concat(Flat("2026-09-15", "2026-09-28", 200));

        var phases = CyclePhases.Of(periods, readings, _utc, _today, 70, 180);

        phases.Cycles.ShouldBe(1);
        var (menstrual, follicular, luteal) = (phases.Menstrual, phases.Follicular, phases.Luteal);
        menstrual.Mean!.Value.ShouldBe(150, 1e-6);
        follicular.Mean!.Value.ShouldBe(110, 1e-6);
        follicular.InRange!.Value.ShouldBe(1, 1e-9);
        luteal.Mean!.Value.ShouldBe(200, 1e-6);
        luteal.High!.Value.ShouldBe(1, 1e-9);
        luteal.InRange!.Value.ShouldBe(0, 1e-9);
        luteal.Days.ShouldBe(14);
        luteal.Coverage.ShouldBe(1, 1e-3);
    }

    // Two cycles' luteal days weigh by the minutes each one covered.
    [Fact]
    public void APhaseAcrossCycles_WeighsEachByTheMinutesItCovered()
    {
        var periods = new[] { P("2026-08-04", "2026-08-08"), P("2026-09-01", "2026-09-05"), P("2026-09-29", "2026-10-03") };
        var readings = Flat("2026-08-18", "2026-08-31", 100).Concat(Flat("2026-09-15", "2026-09-21", 220));

        var luteal = CyclePhases.Of(periods, readings, _utc, _today, 70, 180).Luteal;

        // 14 days at 100, 7 at 220: (14 × 100 + 7 × 220) / 21, give or take the 220s' last reading, which
        // stands for the fifteen minutes after it (GlucoseStatistics' cap) where the 100s' meet the end.
        luteal.Mean!.Value.ShouldBe(140, 0.1);
        luteal.Coverage.ShouldBe(21.0 / 28, 1e-3);
    }

    // Under half the phase's time with readings says nothing: a few days would be read as her pattern.
    [Fact]
    public void APhaseWithReadingsForLessThanHalfItsDays_HasNoFigures()
    {
        var periods = new[] { P("2026-09-01", "2026-09-05"), P("2026-09-29", "2026-10-03") };

        var luteal = CyclePhases.Of(periods, Flat("2026-09-15", "2026-09-20", 200), _utc, _today, 70, 180).Luteal;

        luteal.Coverage.ShouldBeLessThan(0.5);
        luteal.Mean.ShouldBeNull();
        luteal.InRange.ShouldBeNull();
    }

    // A sensor worn only through her latest cycle still reads that cycle: the floor is each cycle's,
    // never all six pooled, and only the cycles read are counted.
    [Fact]
    public void ASensorWornForTheLatestCycleOnly_ReadsThatCycle_AndCountsOne()
    {
        var periods = Enumerable.Range(0, 7)
            .Select(n => D("2026-04-07").AddDays(28 * n))
            .Select(start => new CyclePeriod(start, start.AddDays(4)))
            .ToList();
        var latest = CyclePhases.Cycles(periods, _today)[^1];
        var readings = Flat(latest.Luteal.First.ToString("yyyy-MM-dd"), latest.Luteal.Last.ToString("yyyy-MM-dd"), 190);

        var phases = CyclePhases.Of(periods, readings, _utc, _today, 70, 180);

        phases.Cycles.ShouldBe(1);
        phases.Luteal.Mean!.Value.ShouldBe(190, 1e-6);
        phases.Luteal.Days.ShouldBe(14);
        phases.Follicular.Mean.ShouldBeNull();
    }
}