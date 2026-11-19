using Domain.DTOs.Sugary;

namespace McpServerSugary.Glucose;

public sealed record GlucosePoint(DateTimeOffset At, double MgPerDl);

// The curve the app draws, kept small for a phone: a day or two as read, a week or two as quarter-hour
// means (about 1,300 points for fourteen days, where five-minute means would be four thousand on a
// chart a few hundred pixels wide), longer as hourly means (about 2,200 for ninety days). A bucket
// with no reading is left out, so the app breaks the line where the sensor was silent.
public static class GlucoseCurve
{
    public static readonly TimeSpan LongestWindow = TimeSpan.FromDays(90);

    public static TimeSpan? BucketFor(TimeSpan window) =>
        window <= TimeSpan.FromDays(2) ? null
        : window <= TimeSpan.FromDays(16) ? TimeSpan.FromMinutes(15)
        : TimeSpan.FromHours(1);

    // Buckets are aligned on UTC, each point at its bucket's middle.
    public static IReadOnlyList<GlucosePoint> Of(IEnumerable<SensorReading> readings, TimeSpan? bucket)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var ordered = readings.OrderBy(r => r.At);
        if (bucket is not { } size)
        {
            return [.. ordered.Select(r => new GlucosePoint(r.At, r.MgPerDl))];
        }

        return
        [
            .. ordered
                .GroupBy(r => r.At.UtcTicks / size.Ticks)
                .Select(g => new GlucosePoint(
                    new DateTimeOffset(g.Key * size.Ticks, TimeSpan.Zero) + size / 2,
                    Math.Round(g.Average(r => r.MgPerDl), 1)))
        ];
    }
}