using Domain.DTOs.Sugary;

namespace Domain.Tools.Sugary;

// A sensor's minute values jitter and are now and then simply wrong: 90, 60, 80 in three minutes is no
// low, since blood glucose cannot fall and recover that fast. Each reading becomes the median of itself
// and up to two readings each side, so a misread vanishes where an average would smear it over its
// neighbours and shave a real low. A 15-minute average erased simulated lows reaching 66 entirely; this
// window keeps their minutes under 70.
//
// The window is as wide each way: one-sided, it would pull a reading towards where glucose had been. So
// the newest reading stands as the sensor gave it and settles as the minutes after it arrive, and a
// reading beside a gap, or a sparse one (a backfilled curve point, an hourly mean), is left as it is.
//
// For what a person reads back. An alarm judges the raw reading, since a real low must not wait on this.
public static class GlucoseSmoothing
{
    private const int MostEachSide = 2;

    // Live readings are a minute apart and take two each side; Home Assistant's imported history is mostly
    // three or four apart and takes one. Short of five, because on a five-minute curve one point is five
    // minutes of glucose: a real low can be a single point there, and a median would erase it.
    public static readonly TimeSpan Reach = TimeSpan.FromMinutes(4.5);

    public static IReadOnlyList<SensorReading> Of(IEnumerable<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var ordered = readings.OrderBy(r => r.At).ToArray();
        return [.. ordered.Select((reading, index) => reading with { MgPerDl = MedianAround(ordered, index) })];
    }

    // Loops and a stack window, not LINQ: the mount runs this over a whole log, half a million readings a
    // year, on every statement that reads glucose.
    private static double MedianAround(SensorReading[] ordered, int index)
    {
        var side = Math.Min(Neighbours(ordered, index, -1), Neighbours(ordered, index, 1));
        Span<double> window = stackalloc double[2 * side + 1];
        for (var offset = -side; offset <= side; offset++)
        {
            window[offset + side] = ordered[index + offset].MgPerDl;
        }

        window.Sort();
        return window[side];
    }

    private static int Neighbours(SensorReading[] ordered, int index, int direction)
    {
        var count = 0;
        while (count < MostEachSide
               && index + direction * (count + 1) is >= 0 and var next && next < ordered.Length
               && (ordered[next].At - ordered[index].At).Duration() <= Reach)
        {
            count++;
        }

        return count;
    }
}