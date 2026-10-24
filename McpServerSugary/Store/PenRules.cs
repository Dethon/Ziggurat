namespace McpServerSugary.Store;

// What makes a pen one the app keeps, and which reminder an open pen is owed today. Each problem is
// the sentence the refusal says, or null.
public static class PenRules
{
    // Rapid pens last about four weeks open and the longest-lasting basal ones eight; this catches a
    // slipped digit, not a clinical limit.
    public const int MaxDays = 120;

    public const int MaxInsulin = 40;

    // Her phone can be a day ahead of the home's calendar, travelling east.
    public const int FutureSlackDays = 1;

    public const string NoInsulin = "A pen needs the insulin's name.";
    public const string InsulinTooLong = "The insulin's name can be at most 40 characters.";
    public const string NoOpenedOn = "A pen needs the day it was first used.";
    public const string OpenedInFuture = "A pen can't be first used after today.";
    public const string DaysOutOfRange = "A pen lasts between 1 and 120 days.";

    public static string? Problem(string? insulin, DateOnly? openedOn, int? days, DateOnly today) =>
        (insulin?.Trim(), openedOn, days) switch
        {
            (null or "", _, _) => NoInsulin,
            ({ Length: > MaxInsulin }, _, _) => InsulinTooLong,
            (_, null, _) => NoOpenedOn,
            (_, { } opened, _) when opened > today.AddDays(FutureSlackDays) => OpenedInFuture,
            (_, _, null or < 1 or > MaxDays) => DaysOutOfRange,
            _ => null
        };

    // The day before she is told it is tomorrow; from the day on, that it is today, so a server that
    // was down on the day still tells her once it is back. A missed day-before is not sent late.
    public static PenReminder? Due(Pen pen, DateOnly today) => pen switch
    {
        { ReplacedOn: not null } => null,
        _ when today >= pen.ReplaceOn => pen.TodaySentFor == pen.ReplaceOn ? null : PenReminder.Today,
        _ when today == pen.ReplaceOn.AddDays(-1) => pen.TomorrowSentFor == pen.ReplaceOn ? null : PenReminder.Tomorrow,
        _ => null
    };
}