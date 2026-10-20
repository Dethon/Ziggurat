using System.Globalization;

namespace McpServerSugary.Store;

// What makes a period one the log keeps. Each answer is the sentence the refusal says, or null. The
// rules refuse and never move a day she gave: the days kept are always the ones she chose.
public static class PeriodRules
{
    // Long enough for any period, short enough to catch an end a month out by a slip.
    public const int MaxDays = 31;

    // Her phone can be a day ahead of the home's calendar, travelling east.
    public const int FutureSlackDays = 1;

    public const string NoStart = "A period needs the day it started.";

    public static string? Problem(DateOnly? start, DateOnly? end, DateOnly today, IEnumerable<Period> others)
    {
        if (start is not { } first)
        {
            return NoStart;
        }

        var latest = today.AddDays(FutureSlackDays);
        return end switch
        {
            { } last when last < first => "A period can't end before it starts.",
            _ when first > latest || end > latest => "A period can't start or end after today.",
            { } last when last.DayNumber - first.DayNumber + 1 > MaxDays => $"A period can be at most {MaxDays} days.",
            _ => Overlap(first, end, others)
        };
    }

    // No day is in two periods. An ongoing period reaches every day after its start, so the one
    // before a new period is named as still going rather than as overlapping it.
    private static string? Overlap(DateOnly start, DateOnly? end, IEnumerable<Period> others) =>
        others
            .Where(other => other.Start <= (end ?? DateOnly.MaxValue) && start <= (other.End ?? DateOnly.MaxValue))
            .OrderBy(other => other.Start)
            .Select(other => other.End is null && other.Start < start
                ? $"The period that started on {Day(other.Start)} hasn't ended. End it first."
                : $"It overlaps the period that started on {Day(other.Start)}.")
            .FirstOrDefault();

    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}