using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// A sensor's minute values jitter, and now and then one is simply wrong: 90, 60, 80 in three minutes is no
// low, since blood glucose cannot fall and recover that fast. The median of the readings around each one
// drops such a value and leaves a low that lasted.
public class GlucoseSmoothingTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

    private static SensorReading At(double minutes, double mgPerDl, GlucoseTrend trend = GlucoseTrend.Unknown) =>
        new(_start.AddMinutes(minutes), mgPerDl, trend);

    private static SensorReading[] Minutes(params double[] values) =>
        [.. values.Select((value, minute) => At(minute, value))];

    private static double[] Values(IEnumerable<SensorReading> readings) => [.. readings.Select(r => r.MgPerDl)];

    [Fact]
    public void AOneMinuteMisread_IsGone()
    {
        var smoothed = GlucoseSmoothing.Of(Minutes(85, 85, 85, 90, 60, 80, 85, 85, 85));

        smoothed[4].MgPerDl.ShouldBe(85);
        Values(smoothed).Min().ShouldBeGreaterThanOrEqualTo(80);
    }

    [Fact]
    public void TwoMisreadsInARow_AreGone()
    {
        var smoothed = GlucoseSmoothing.Of(Minutes(100, 100, 100, 100, 60, 58, 100, 100, 100, 100));

        Values(smoothed).ShouldAllBe(value => value == 100);
    }

    [Fact]
    public void ALowThatLasted_StaysLow()
    {
        double[] low = [90, 84, 78, 72, 68, 64, 62, 61, 62, 65, 69, 73, 79, 85, 90];

        var smoothed = GlucoseSmoothing.Of(Minutes(low));

        Values(smoothed).Count(value => value < 70).ShouldBe(low.Count(value => value < 70));
        Values(smoothed).Min().ShouldBeInRange(61, 62);
    }

    [Fact]
    public void ASteadyFall_IsLeftAsItIs()
    {
        double[] falling = [120, 117, 114, 111, 108, 105, 102, 99];

        Values(GlucoseSmoothing.Of(Minutes(falling))).ShouldBe(falling);
    }

    // The line ends on the number at the top of the tab, and a reading only settles once the minutes
    // after it have come.
    [Fact]
    public void TheNewestReading_IsLeftAsItIs()
    {
        var smoothed = GlucoseSmoothing.Of(Minutes(100, 100, 100, 100, 62));

        smoothed[^1].MgPerDl.ShouldBe(62);
    }

    // Looking at one side only would pull a reading towards where glucose was.
    [Fact]
    public void AReadingNearTheEnd_LooksAsFarEachWay()
    {
        var smoothed = GlucoseSmoothing.Of(Minutes(100, 104, 108, 112, 116));

        smoothed[3].MgPerDl.ShouldBe(112);
    }

    // Home Assistant's history keeps a value about every three minutes.
    [Fact]
    public void AMisreadAmongReadingsThreeMinutesApart_IsGone()
    {
        SensorReading[] imported = [At(0, 100), At(3, 100), At(6, 70), At(9, 100), At(12, 100)];

        GlucoseSmoothing.Of(imported)[2].MgPerDl.ShouldBe(100);
    }

    // On a five-minute curve one point is five minutes of glucose, so a real low can be a single point.
    [Fact]
    public void ALowOfOnePointOnAFiveMinuteCurve_StaysLow()
    {
        SensorReading[] curve = [At(0, 82), At(5, 76), At(10, 66), At(15, 75), At(20, 83)];

        Values(GlucoseSmoothing.Of(curve)).ShouldBe([82, 76, 66, 75, 83]);
    }

    [Fact]
    public void ReadingsTooFarApart_AreLeftAsTheyAre()
    {
        SensorReading[] backfill = [At(0, 100), At(15, 60), At(30, 110), At(90, 180)];

        Values(GlucoseSmoothing.Of(backfill)).ShouldBe([100, 60, 110, 180]);
    }

    [Fact]
    public void AGap_IsNotSmoothedAcross()
    {
        SensorReading[] readings = [At(0, 100), At(1, 100), At(2, 100), At(30, 60), At(31, 60), At(32, 60)];

        Values(GlucoseSmoothing.Of(readings)).ShouldBe([100, 100, 100, 60, 60, 60]);
    }

    [Fact]
    public void TheResult_IsOldestFirst_WithEachReadingsTimeAndArrow()
    {
        SensorReading[] readings = [At(2, 85, GlucoseTrend.Rising), At(0, 85, GlucoseTrend.Steady), At(1, 85, GlucoseTrend.Falling)];

        var smoothed = GlucoseSmoothing.Of(readings);

        smoothed.ShouldBe([At(0, 85, GlucoseTrend.Steady), At(1, 85, GlucoseTrend.Falling), At(2, 85, GlucoseTrend.Rising)]);
    }

    private static double Rms(IEnumerable<double> errors) => Math.Sqrt(errors.Average(error => error * error));

    // The swings a median leaves alone: a few mg/dL, a few minutes long, on a curve that moves slowly.
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    public void ASensorsWobble_IsAveragedOut(int period)
    {
        static double Truth(int minute) => 120 + 30 * Math.Sin(minute / 60.0);
        var wobbly = Enumerable.Range(0, 240)
            .Select(minute => At(minute, Truth(minute) + 4 * Math.Sin(minute * 2 * Math.PI / period)))
            .ToArray();

        var smoothed = GlucoseSmoothing.Of(wobbly);

        var middle = Enumerable.Range(60, 120).ToList();
        Rms(middle.Select(m => smoothed[m].MgPerDl - Truth(m)))
            .ShouldBeLessThan(Rms(middle.Select(m => wobbly[m].MgPerDl - Truth(m))) / 4);
    }

    // A dip to 65 from 120; the narrowest spends about seven minutes under 70.
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(15)]
    public void ARealLow_StaysUnderSeventy(int width)
    {
        var low = Enumerable.Range(0, 180)
            .Select(minute => At(minute, 120 - 55 * Math.Exp(-Math.Pow(minute - 90, 2) / (2 * width * width))))
            .ToArray();

        Values(GlucoseSmoothing.Of(low)).Min().ShouldBeLessThan(70);
    }

    // Seven minutes under 70 in an otherwise flat day: a wide fit alone reads it as 74.
    [Fact]
    public void AShortLowInAFlatDay_StaysUnderSeventy()
    {
        double[] day =
        [
            .. Enumerable.Repeat(100.0, 30),
            90, 84, 78, 72, 68, 64, 62, 61, 62, 65, 69, 73, 79, 85, 90,
            .. Enumerable.Repeat(100.0, 30)
        ];

        var smoothed = Values(GlucoseSmoothing.Of(Minutes(day)));

        smoothed.Count(value => value < 70).ShouldBeGreaterThanOrEqualTo(5);
        smoothed.Min().ShouldBeLessThanOrEqualTo(66);
    }

    [Fact]
    public void AMealsPeak_KeepsItsHeight()
    {
        var meal = Enumerable.Range(0, 240)
            .Select(minute => At(minute, 120 + 80 * Math.Exp(-Math.Pow(minute - 120, 2) / (2 * 25 * 25))))
            .ToArray();

        Values(GlucoseSmoothing.Of(meal)).Max().ShouldBeGreaterThan(197);
    }

    [Fact]
    public void NoReadings_AreNoReadings() => GlucoseSmoothing.Of([]).ShouldBeEmpty();
}