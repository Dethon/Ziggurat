using Domain.DTOs.Sugary;

namespace McpServerSugary.Glucose;

// The figures a clinic reads off a sensor over a period. Shares are of the time with readings, not of
// the period, and the bands are cumulative as the international consensus reports them: under 54 is
// also under 70, over 250 also over 180. In range is her own range; the bands below and above are the
// consensus thresholds whatever her range. Every figure but the count and the coverage is null with no
// readings, since there is nothing to say and NaN is no answer.
public sealed record GlucoseFigures(
    int Count,
    double Coverage,
    double? Mean,
    double? Gmi,
    double? Cv,
    double? VeryLow,
    double? Low,
    double? InRange,
    double? High,
    double? VeryHigh);

// One implementation for the app's statistics. Each reading stands for the minutes until the next, at
// most Cap, and the last for what is left of the period: readings are kept unevenly (the current one
// about every minute, the curve's every five where there was none, an import's every hour), so a plain mean would
// weigh a stretch by how often it was read, and a gap in the sensor would count for the values either
// side of it. The mean and the SD are weighed the same way, so CV and GMI rest on the same minutes.
public static class GlucoseStatistics
{
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(15);

    public const double VeryLowBelow = 54;
    public const double LowBelow = 70;
    public const double HighAbove = 180;
    public const double VeryHighAbove = 250;

    public static GlucoseFigures Of(
        IEnumerable<SensorReading> readings, DateTimeOffset from, DateTimeOffset to, int targetLow, int targetHigh)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var inPeriod = readings.Where(r => r.At >= from && r.At < to).OrderBy(r => r.At).ToList();
        var weighed = inPeriod
            .Select((reading, i) =>
            {
                var next = i + 1 < inPeriod.Count ? inPeriod[i + 1].At : to;
                var span = next - reading.At;
                return (Value: reading.MgPerDl, Minutes: (span < Cap ? span : Cap).TotalMinutes);
            })
            .ToList();

        var covered = weighed.Sum(w => w.Minutes);
        var coverage = to > from ? covered / (to - from).TotalMinutes : 0;
        if (covered <= 0)
        {
            return new GlucoseFigures(inPeriod.Count, coverage, null, null, null, null, null, null, null, null);
        }

        double Share(Func<double, bool> holds) => weighed.Where(w => holds(w.Value)).Sum(w => w.Minutes) / covered;

        var mean = weighed.Sum(w => w.Value * w.Minutes) / covered;
        var sd = Math.Sqrt(weighed.Sum(w => w.Minutes * (w.Value - mean) * (w.Value - mean)) / covered);
        return new GlucoseFigures(
            inPeriod.Count,
            coverage,
            mean,
            Gmi(mean),
            sd / mean,
            Share(v => v < VeryLowBelow),
            Share(v => v < LowBelow),
            Share(v => v >= targetLow && v <= targetHigh),
            Share(v => v > HighAbove),
            Share(v => v > VeryHighAbove));
    }

    // Glucose management indicator, in percent: the HbA1c the mean would usually go with.
    public static double Gmi(double meanMgPerDl) => 3.31 + 0.02392 * meanMgPerDl;
}