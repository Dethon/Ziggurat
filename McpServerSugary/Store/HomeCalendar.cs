namespace McpServerSugary.Store;

// Which day it is at home: a period or a note is a day on her calendar, and whether that day is
// still to come is a question for the home's clock, not the server's.
public sealed class HomeCalendar(TimeProvider clock, TimeZoneInfo zone)
{
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
}