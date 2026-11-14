using System.Globalization;

namespace Dashboard.Client.Components;

// When something happened, as every dashboard page writes it: day first, a 24-hour clock, in the
// reader's own zone. An event's timestamp arrives as its publisher stamped it, in UTC, and written
// as it came it read an hour or more off the Households page and the owners' admin pages beside it.
public static class DashboardTime
{
    // An event in a feed or a table, to the second.
    public static string Event(DateTimeOffset at) => Event(at, TimeZoneInfo.Local);

    public static string Event(DateTimeOffset at, TimeZoneInfo zone) => Write(at, zone, "dd/MM HH:mm:ss");

    // When a state began, to the minute: a household last seen, suspended, a server connected.
    public static string Moment(DateTimeOffset at) => Moment(at, TimeZoneInfo.Local);

    public static string Moment(DateTimeOffset at, TimeZoneInfo zone) => Write(at, zone, "dd/MM HH:mm");

    // Until when something holds, with its year: a link's expiry.
    public static string Deadline(DateTimeOffset at) => Deadline(at, TimeZoneInfo.Local);

    public static string Deadline(DateTimeOffset at, TimeZoneInfo zone) => Write(at, zone, "dd/MM/yyyy HH:mm");

    private static string Write(DateTimeOffset at, TimeZoneInfo zone, string format) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString(format, CultureInfo.InvariantCulture);
}