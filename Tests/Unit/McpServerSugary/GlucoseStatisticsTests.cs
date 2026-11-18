using Domain.DTOs.Sugary;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// What a clinic reads off a sensor, over a period: the share of the time below 54 and 70, in her
// range, above 180 and 250, the mean, GMI, CV and how much of the period had readings. Each reading
// stands for the minutes until the next one, at most fifteen, so a gap in the sensor counts for
// nothing rather than for the value either side of it; the figures here are worked by hand.
public class GlucoseStatisticsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

    private static SensorReading At(double minutes, double mgPerDl) =>
        new(Start.AddMinutes(minutes), mgPerDl, GlucoseTrend.Unknown);

    private static GlucoseFigures Over(double minutes, params SensorReading[] readings) =>
        GlucoseStatistics.Of(readings, Start, Start.AddMinutes(minutes), 70, 180);

    [Fact]
    public void EvenReadings_WeighAlike()
    {
        var figures = Over(20, At(0, 60), At(5, 100), At(10, 200), At(15, 260));

        figures.Count.ShouldBe(4);
        figures.Coverage.ShouldBe(1, 1e-9);
        figures.VeryLow!.Value.ShouldBe(0, 1e-9);
        figures.Low!.Value.ShouldBe(0.25, 1e-9);
        figures.InRange!.Value.ShouldBe(0.25, 1e-9);
        figures.High!.Value.ShouldBe(0.5, 1e-9);
        figures.VeryHigh!.Value.ShouldBe(0.25, 1e-9);
        figures.Mean!.Value.ShouldBe(155, 1e-9);
        // 3.31 + 0.02392 × 155
        figures.Gmi!.Value.ShouldBe(7.0176, 1e-9);
        // deviations −95, −55, 45, 105: variance 25100 / 4 = 6275
        figures.Cv!.Value.ShouldBe(Math.Sqrt(6275) / 155, 1e-9);
    }

    // The clinic's bands are cumulative: under 54 is also under 70, over 250 is also over 180.
    [Fact]
    public void TheBands_AreCumulative()
    {
        var figures = Over(20, At(0, 40), At(5, 50), At(10, 300), At(15, 320));

        figures.VeryLow!.Value.ShouldBe(0.5, 1e-9);
        figures.Low!.Value.ShouldBe(0.5, 1e-9);
        figures.High!.Value.ShouldBe(0.5, 1e-9);
        figures.VeryHigh!.Value.ShouldBe(0.5, 1e-9);
        figures.InRange!.Value.ShouldBe(0, 1e-9);
    }

    // 54 is not very low, 70 and 180 are in range, 250 is high but not very high.
    [Fact]
    public void TheBoundaries_FallOnTheGentlerSide()
    {
        var figures = Over(20, At(0, 54), At(5, 70), At(10, 180), At(15, 250));

        figures.VeryLow!.Value.ShouldBe(0, 1e-9);
        figures.Low!.Value.ShouldBe(0.25, 1e-9);
        figures.InRange!.Value.ShouldBe(0.5, 1e-9);
        figures.High!.Value.ShouldBe(0.25, 1e-9);
        figures.VeryHigh!.Value.ShouldBe(0, 1e-9);
    }

    // Readings kept a few minutes apart (the current reading) and five apart (the curve) do not count
    // alike: the mean is of the time, not of the readings. Plain, it would be 133.3.
    [Fact]
    public void UnevenReadings_WeighTheirMinutes()
    {
        var figures = Over(16, At(0, 100), At(3, 100), At(6, 200));

        figures.Mean!.Value.ShouldBe(162.5, 1e-9);
        figures.InRange!.Value.ShouldBe(6.0 / 16, 1e-9);
        figures.High!.Value.ShouldBe(10.0 / 16, 1e-9);
        // weighted deviations: 6 min at −62.5, 10 min at 37.5 → (6·3906.25 + 10·1406.25) / 16
        figures.Cv!.Value.ShouldBe(Math.Sqrt((6 * 3906.25 + 10 * 1406.25) / 16) / 162.5, 1e-9);
    }

    // A gap counts for nothing: each side of it stands for fifteen minutes at most.
    [Fact]
    public void AGap_IsNotCovered()
    {
        var figures = Over(75, At(0, 100), At(60, 200));

        figures.Coverage.ShouldBe(30.0 / 75, 1e-9);
        figures.Mean!.Value.ShouldBe(150, 1e-9);
        figures.InRange!.Value.ShouldBe(0.5, 1e-9);
        figures.High!.Value.ShouldBe(0.5, 1e-9);
    }

    // The last reading stands only for what is left of the period.
    [Fact]
    public void TheLastReading_EndsWithThePeriod()
    {
        var figures = Over(12, At(0, 100), At(10, 200));

        figures.Coverage.ShouldBe(1, 1e-9);
        figures.Mean!.Value.ShouldBe((10 * 100 + 2 * 200) / 12.0, 1e-9);
        figures.High!.Value.ShouldBe(2.0 / 12, 1e-9);
    }

    // One reading in a day: its figures stand, and the coverage says how little they rest on.
    [Fact]
    public void AFewReadings_AreLowCoverage()
    {
        var figures = Over(24 * 60, At(600, 120));

        figures.Count.ShouldBe(1);
        figures.Coverage.ShouldBe(15.0 / 1440, 1e-9);
        figures.Mean!.Value.ShouldBe(120, 1e-9);
        figures.InRange!.Value.ShouldBe(1, 1e-9);
        figures.Cv!.Value.ShouldBe(0, 1e-9);
    }

    [Fact]
    public void NoReadings_HaveNoFigures()
    {
        var figures = Over(60);

        figures.Count.ShouldBe(0);
        figures.Coverage.ShouldBe(0);
        figures.Mean.ShouldBeNull();
        figures.Gmi.ShouldBeNull();
        figures.Cv.ShouldBeNull();
        figures.InRange.ShouldBeNull();
        figures.VeryLow.ShouldBeNull();
        figures.VeryHigh.ShouldBeNull();
    }

    [Fact]
    public void ReadingsOutsideThePeriod_AreLeftOut()
    {
        var figures = Over(10, At(-5, 300), At(0, 100), At(5, 100), At(10, 300));

        figures.Count.ShouldBe(2);
        figures.Mean!.Value.ShouldBe(100, 1e-9);
        figures.High!.Value.ShouldBe(0, 1e-9);
    }

    // Readings arrive in any order; the minutes each stands for are read in time order.
    [Fact]
    public void ReadingsOutOfOrder_AreSorted()
    {
        var figures = Over(16, At(6, 200), At(0, 100), At(3, 100));

        figures.Mean!.Value.ShouldBe(162.5, 1e-9);
    }

    // In range is her range; the clinic's bands below and above stay where they are.
    [Fact]
    public void InRange_IsHerRange()
    {
        var figures = GlucoseStatistics.Of(
            [At(0, 75), At(5, 100), At(10, 150), At(15, 175)], Start, Start.AddMinutes(20), 80, 160);

        figures.InRange!.Value.ShouldBe(0.5, 1e-9);
        figures.Low!.Value.ShouldBe(0, 1e-9);
        figures.High!.Value.ShouldBe(0, 1e-9);
    }
}