namespace McpServerSugary.Store;

// Which day it is at home: a period or a note is a day on her calendar, and whether that day is
// still to come is a question for the home's clock, not the server's. A time told back to her is
// told on the same clock.
public sealed class HomeCalendar(TimeProvider clock, TimeZoneInfo zone)
{
    public DateTimeOffset Now => clock.GetUtcNow();

    public DateOnly Today => DateOnly.FromDateTime(AtHome(Now).DateTime);

    public DateTimeOffset AtHome(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, zone);
}