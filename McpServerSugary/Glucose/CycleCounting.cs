namespace McpServerSugary.Glucose;

// One of her cycles as counted: Skipped is a period she likely did not note (counted in nothing), Joined
// a cycle running across a start read as spotting.
public sealed record CountedCycle(DateOnly Start, int Days, bool Skipped, bool Joined);

// Anchor is the start her current cycle runs from; null before any.
public sealed record CountedCycles(IReadOnlyList<CountedCycle> Cycles, DateOnly? Anchor);

// Which of her starts make her cycles, and which cycles count: the app's rule (cycle.ts, countCycles) to
// the day, both pinned to Sugary.Client/src/cases/cycles.json; a change to one is made to the other.
// Her last twelve cycles, judged against their median from three on: half again longer is a period she
// did not note; shorter by as much is spotting, its start dropped — of its two ends the one whose joined
// cycle comes nearest the median, the latest start when it is the latest cycle. Under three, a cycle over
// 45 days is a period she did not note.
public static class CycleCounting
{
    private const int CountedCycles = 12;
    private const double Outlier = 1.5;
    private const int JudgedFrom = 3;
    private const int LongestAlone = 45;

    public static CountedCycles Of(IEnumerable<DateOnly> days)
    {
        var starts = days.Distinct().Order().TakeLast(CountedCycles + 1).ToList();
        var judged = starts.Count - 1 >= JudgedFrom;
        var usual = judged ? Median(Lengths(starts)) : 0;
        var joined = new HashSet<DateOnly>();
        while (judged && starts.Count >= 2)
        {
            var cycles = Lengths(starts);
            var shortest = cycles.FindIndex(days => days < usual / Outlier);
            if (shortest < 0)
            {
                break;
            }

            if (shortest == cycles.Count - 1)
            {
                starts.RemoveAt(shortest + 1);
            }
            else if (shortest == 0
                     || Math.Abs(cycles[shortest] + cycles[shortest + 1] - usual) < Math.Abs(cycles[shortest - 1] + cycles[shortest] - usual))
            {
                joined.Add(starts[shortest]);
                starts.RemoveAt(shortest + 1);
            }
            else
            {
                joined.Add(starts[shortest - 1]);
                starts.RemoveAt(shortest);
            }
        }

        return new CountedCycles(
            [
                .. Lengths(starts).Select((days, index) => new CountedCycle(
                    starts[index], days, judged ? days > usual * Outlier : days > LongestAlone, joined.Contains(starts[index])))
            ],
            starts.Count > 0 ? starts[^1] : null);
    }

    public static double Median(IReadOnlyList<int> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    private static List<int> Lengths(List<DateOnly> starts) =>
        [.. starts.Zip(starts.Skip(1), (a, b) => b.DayNumber - a.DayNumber)];
}