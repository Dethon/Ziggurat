using System.Globalization;

namespace Observability;

// What a read covers: every event stamped at or after From and before To. Two shapes reach it. The
// dashboard asks for the last ?days=N, a rolling window resolved here against the server's clock on
// every request, so a reload or a catch-up hours later reads the window as it stands then rather than
// the one its first load saw, and "the last day" is always twenty-four hours whatever the time.
// Other callers (an owner's usage, Lemonade's use) ask for whole calendar days, ?from= and ?to=.
public sealed record MetricDateRange(DateTimeOffset From, DateTimeOffset To)
{
    public static MetricDateRange Spanning(DateOnly from, DateOnly to) => new(StartOf(from), StartOf(to.AddDays(1)));

    // The window has no end before today's: an event stamped a moment after the request, by a host
    // whose clock runs ahead of this one, still belongs to it.
    public static MetricDateRange Last(int days, DateTimeOffset now) =>
        new(now.AddDays(-days), StartOf(DateOnly.FromDateTime(now.UtcDateTime).AddDays(1)));

    // The UTC days the window touches: the daily keys a read opens. A read off the day-granular totals
    // hash counts each of them whole.
    public IEnumerable<DateOnly> Dates
    {
        get
        {
            var first = DateOnly.FromDateTime(From.UtcDateTime);
            var last = DateOnly.FromDateTime(To.AddTicks(-1).UtcDateTime);
            return Enumerable.Range(0, Math.Max(0, last.DayNumber - first.DayNumber + 1)).Select(first.AddDays);
        }
    }

    public TimeSpan Length => To - From;

    public bool Contains(DateTimeOffset at) => at >= From && at < To;

    public static ValueTask<MetricDateRange?> BindAsync(HttpContext context)
    {
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        if (context.Request.Query.TryGetValue("days", out var rawDays))
        {
            return ValueTask.FromResult(
                int.TryParse(rawDays.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0
                    ? Last(days, now)
                    : null);
        }

        return ValueTask.FromResult(
            Read(context, "from", today) is { } from && Read(context, "to", today) is { } to
                ? Spanning(from, to)
                : null);
    }

    private static DateTimeOffset StartOf(DateOnly date) => new(date, TimeOnly.MinValue, TimeSpan.Zero);

    // A value that is present but will not parse yields null, which the framework answers with the
    // same 400 the two nullable DateOnly parameters gave. Only an absent value takes the default.
    private static DateOnly? Read(HttpContext context, string key, DateOnly fallback) =>
        !context.Request.Query.TryGetValue(key, out var raw)
            ? fallback
            : DateOnly.TryParse(raw.ToString(), CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
}