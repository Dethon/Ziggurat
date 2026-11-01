using System.Globalization;

namespace WebChat.Client.Components.Admin;

// When something happened, as the operator's dashboard writes it: day first, a 24-hour clock, in the
// reader's own zone — never by the browser's culture, which wrote "11/1/2026 11:03 PM" beside the
// dashboard's "01/11/2026 23:03".
public static class AdminTime
{
    public static string Of(DateTimeOffset at) => Of(at, TimeZoneInfo.Local);

    public static string Of(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}