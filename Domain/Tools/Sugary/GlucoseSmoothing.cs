using Domain.DTOs.Sugary;

namespace Domain.Tools.Sugary;

// What a person reads back of a sensor that jitters, wanders by a few mg/dL and is now and then simply
// wrong: 90, 60, 80 in three minutes is no low, since blood glucose cannot fall and recover that fast.
// Two steps, every reading kept. A median of it and up to two readings each side takes a false spike out
// whole, where an average would smear it over its neighbours. A curve fitted through the minutes around it
// then averages the wander out; a quartic rather than a mean, because a mean of that width cuts the bottom
// off a real low and the top off a meal's peak, and a curve can bend with them. Even so, a fit wide enough
// to calm the line flattens a short real low, so it may move a reading only MostMoved from the first step's
// value: however brief, a low of 65 or less still reads as under 70.
//
// Both look as far each way: one-sided, they would pull a reading towards where glucose had been. So the
// newest reading stands as the sensor gave it and settles as the minutes after it arrive, and a stretch
// stops at a gap. Readings five or more minutes apart are never joined: such a point is already a curve's
// or an hour's average, and one point of it can be a real low.
//
// An alarm judges the raw reading, since a real low must not wait on this.
public static class GlucoseSmoothing
{
    private const int MostEachSide = 2;
    private const int MostDegree = 4;
    private const double MostMoved = 4;

    // Live readings are a minute apart and take two each side; Home Assistant's imported history is mostly
    // three or four apart and takes one. Also the widest step a fitted stretch crosses.
    public static readonly TimeSpan Reach = TimeSpan.FromMinutes(4.5);

    private static readonly TimeSpan _fitReach = TimeSpan.FromMinutes(30);

    // How far beyond a slice its readings must be read, so an instant reads the same in any window.
    public static readonly TimeSpan Margin = _fitReach + Reach;

    public static IReadOnlyList<SensorReading> Of(IEnumerable<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var ordered = readings.OrderBy(r => r.At).ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var minutes = ordered.Select(r => (r.At - ordered[0].At).TotalMinutes).ToArray();
        var despiked = ordered.Select((_, index) => MedianAround(ordered, index)).ToArray();
        var fitted = new double[ordered.Length];
        Parallel.For(0, ordered.Length, index => fitted[index] = Fitted(minutes, despiked, index));
        return [.. ordered.Select((reading, index) => reading with { MgPerDl = fitted[index] })];
    }

    // Loops and a stack window, not LINQ, here and in Fitted: the mount runs this over a whole log, half a
    // million readings a year, on every statement that reads glucose.
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

    // The value at this reading of a weighted least-squares polynomial through its stretch, the nearest
    // minutes weighing most (tricube). A stretch cut short by the newest reading or a gap holds few readings
    // and takes a lower degree, or the curve would run through every one of them.
    private static double Fitted(double[] minutes, double[] values, int index)
    {
        var at = minutes[index];
        var (reach, joined) = (_fitReach.TotalMinutes, Reach.TotalMinutes);
        var (first, last) = (index, index);
        while (first > 0 && at - minutes[first - 1] <= reach && minutes[first] - minutes[first - 1] <= joined)
        {
            first--;
        }

        while (last < minutes.Length - 1 && minutes[last + 1] - at <= reach && minutes[last + 1] - minutes[last] <= joined)
        {
            last++;
        }

        var half = Math.Min(at - minutes[first], minutes[last] - at);
        if (half <= 0)
        {
            return values[index];
        }

        Span<double> powers = stackalloc double[2 * MostDegree + 1];
        Span<double> targets = stackalloc double[MostDegree + 1];
        powers.Clear();
        targets.Clear();
        var used = 0;
        for (var j = first; j <= last; j++)
        {
            var d = minutes[j] - at;
            if (Math.Abs(d) > half)
            {
                continue;
            }

            used++;
            var u = Math.Abs(d) / (half + 1);
            var near = 1 - u * u * u;
            var term = near * near * near;
            var x = d / half;
            for (var k = 0; k <= 2 * MostDegree; k++)
            {
                powers[k] += term;
                if (k <= MostDegree)
                {
                    targets[k] += term * values[j];
                }

                term *= x;
            }
        }

        var size = Math.Min(MostDegree, (used - 1) / 2) + 1;
        return InterceptOf(powers, targets[..size]) is { } fitted
            ? Math.Round(Math.Clamp(fitted, values[index] - MostMoved, values[index] + MostMoved), 1)
            : values[index];
    }

    // The constant term of the normal equations, by elimination with partial pivoting; null when singular.
    private static double? InterceptOf(ReadOnlySpan<double> powers, Span<double> right)
    {
        var size = right.Length;
        Span<double> matrix = stackalloc double[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                matrix[row * size + column] = powers[row + column];
            }
        }

        for (var pivot = size - 1; pivot >= 0; pivot--)
        {
            var best = pivot;
            for (var row = pivot - 1; row >= 0; row--)
            {
                if (Math.Abs(matrix[row * size + pivot]) > Math.Abs(matrix[best * size + pivot]))
                {
                    best = row;
                }
            }

            if (Math.Abs(matrix[best * size + pivot]) < 1e-12)
            {
                return null;
            }

            if (best != pivot)
            {
                for (var column = 0; column < size; column++)
                {
                    (matrix[best * size + column], matrix[pivot * size + column]) =
                        (matrix[pivot * size + column], matrix[best * size + column]);
                }

                (right[best], right[pivot]) = (right[pivot], right[best]);
            }

            for (var row = pivot - 1; row >= 0; row--)
            {
                var factor = matrix[row * size + pivot] / matrix[pivot * size + pivot];
                for (var column = 0; column <= pivot; column++)
                {
                    matrix[row * size + column] -= factor * matrix[pivot * size + column];
                }

                right[row] -= factor * right[pivot];
            }
        }

        // Eliminated from the last column back, so the first row now holds the intercept alone.
        return right[0] / matrix[0];
    }
}