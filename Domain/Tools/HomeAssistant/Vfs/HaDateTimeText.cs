using System.Globalization;
using System.Text.RegularExpressions;

namespace Domain.Tools.HomeAssistant.Vfs;

// Date-time strings the served actions hand to Home Assistant. They cross as the caller wrote them:
// Home Assistant reads a naive one in its own time zone, and this process may sit in another, so
// parsing into an instant here would move a window or an alarm by the difference. The arithmetic
// done — an end from a start, a start counted back from an end — keeps the string's own shape:
// separator, precision and offset.
internal static partial class HaDateTimeText
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:sszzz";

    public static string Now(TimeProvider time) =>
        time.GetUtcNow().ToString(Format, CultureInfo.InvariantCulture);

    // Moves a date-time string by a span, giving back the same shape it came in: a naive
    // "2026-09-02 21:30:00" stays naive with its space, a "2026-09-03T07:00:00+02:00" keeps its
    // offset, a trailing Z stays a Z.
    public static string Shift(string dateTime, TimeSpan by, string argument)
    {
        var trimmed = dateTime.Trim();
        var separator = trimmed.Length > 10 && trimmed[10] == ' ' ? " " : "T";
        var offset = OffsetSuffix().Match(trimmed);

        if (offset.Success)
        {
            if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var aware))
            {
                throw Unparseable(argument, dateTime);
            }
            var moved = aware + by;
            return offset.Value.Equals("Z", StringComparison.OrdinalIgnoreCase)
                ? moved.UtcDateTime.ToString($"yyyy-MM-dd'{separator}'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : moved.ToString($"yyyy-MM-dd'{separator}'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        if (!DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive)
            || trimmed.Length < 16)
        {
            throw Unparseable(argument, dateTime);
        }
        return (naive + by).ToString($"yyyy-MM-dd'{separator}'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string ShiftDate(string date, int days, string argument) =>
        DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : throw new ArgumentException($"--{argument} expects a date as YYYY-MM-DD, got '{date}'.");

    private static ArgumentException Unparseable(string argument, string value) =>
        new($"--{argument} expects a date-time as \"YYYY-MM-DD HH:MM:SS\" (an offset such as +02:00 or Z may follow), got '{value}'. Resolve relative times to an absolute one yourself.");

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetSuffix();
}