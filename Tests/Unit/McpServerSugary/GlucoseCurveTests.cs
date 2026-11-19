using Domain.DTOs.Sugary;
using McpServerSugary.Glucose;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// The curve a phone draws stays small: a day or two as read, a week or two in quarter-hour means,
// longer in hourly means, and a stretch with no reading has no point, so the line breaks there.
public class GlucoseCurveTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

    private static SensorReading At(double minutes, double mgPerDl) =>
        new(Start.AddMinutes(minutes), mgPerDl, GlucoseTrend.Unknown);

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(7, 15)]
    [InlineData(14, 15)]
    [InlineData(30, 60)]
    [InlineData(90, 60)]
    public void TheBucket_GrowsWithTheWindow(int days, int? minutes) =>
        GlucoseCurve.BucketFor(TimeSpan.FromDays(days)).ShouldBe(minutes is { } m ? TimeSpan.FromMinutes(m) : null);

    [Fact]
    public void AShortWindow_IsAsRead()
    {
        var points = GlucoseCurve.Of([At(5, 120), At(0, 100)], null);

        points.ShouldBe([new GlucosePoint(Start, 100), new GlucosePoint(Start.AddMinutes(5), 120)]);
    }

    [Fact]
    public void ALongWindow_IsBucketMeans_AtEachBucketsMiddle()
    {
        var points = GlucoseCurve.Of([At(0, 100), At(5, 110), At(10, 121), At(15, 200)], TimeSpan.FromMinutes(15));

        points.ShouldBe(
        [
            new GlucosePoint(Start.AddMinutes(7.5), 110.3),
            new GlucosePoint(Start.AddMinutes(22.5), 200)
        ]);
    }

    [Fact]
    public void ABucketWithNoReading_HasNoPoint()
    {
        var points = GlucoseCurve.Of([At(0, 100), At(130, 150)], TimeSpan.FromHours(1));

        points.Select(p => p.At).ShouldBe([Start.AddMinutes(30), Start.AddMinutes(150)]);
    }
}