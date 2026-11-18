using System.Globalization;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Import;

public sealed record GlucoseImportRequest(Account Person, string Entity, int Days);

public sealed record GlucoseImportReport(int Imported, int Hourly, int Skipped, int NotReadings);

// A person's glucose from before Sugary read LibreLinkUp, carried over once from Home Assistant: the
// recorder's history where its retention reaches, then the long-term statistics' hourly means for the
// days before the history's first point, which Home Assistant keeps for good. Every value is kept as
// the named person's, in mg/dL and at a UTC instant, and none beside a reading she has: a history
// point within GlucoseReadingRules.Spacing of one is skipped as the poller skips a curve point (the
// ones this run keeps included), and an hourly mean is skipped for any hour she has readings in. So a
// second run adds nothing.
//
// Imported readings never fire a watch: they are written here, by the operator's command in a process
// of its own, and the poll observer is told only what a poll kept.
public static class GlucoseImport
{
    public const double MgPerDlPerMmol = 18.016;

    // A window of the recorder's history per request, so one answer stays small whatever --days is.
    public static readonly TimeSpan Window = TimeSpan.FromDays(10);

    public static async Task<GlucoseImportReport> RunAsync(
        IDbContextFactory<SugaryDbContext> contexts, IHomeAssistantClient home, GlucoseImportRequest request,
        TimeProvider time, CancellationToken ct)
    {
        var entity = request.Entity;
        var state = await home.GetStateAsync(entity, ct)
                    ?? throw new ImportException($"Home Assistant has no entity {entity}.");
        var toMgPerDl = MgPerDlFactor(entity, state);

        var now = ToTheSecond(time.GetUtcNow());
        var from = now - TimeSpan.FromDays(request.Days);
        var history = await HistoryAsync(home, entity, from, now, ct);
        var historyStarts = history.Count > 0 ? history[0].At : now;
        var hours = historyStarts > from
            ? (await home.ListStatisticsAsync(entity, Stamp(from), Stamp(historyStarts), "hour", ct))
                .Where(row => row.Start >= from && row.End <= historyStarts)
                .ToList()
            : [];

        var values = history.Select(change => (change.At, Value: Reading(change.State))).ToList();
        var readings = values.Where(v => v.Value is not null).Select(v => (v.At, MgPerDl: v.Value!.Value * toMgPerDl)).ToList();
        var means = hours.Where(row => Reading(row.Mean) is not null)
            .Select(row => (row.Start, row.End, MgPerDl: row.Mean!.Value * toMgPerDl))
            .ToList();

        await SugaryDatabase.MigrateAsync(contexts, ct);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var (userId, tenantId) = (request.Person.UserId, request.Person.TenantId);
        var spacing = GlucoseReadingRules.Spacing;
        var lowest = from - spacing;
        var highest = now + spacing;
        var instants = new SortedSet<DateTimeOffset>(await db.GlucoseReadings.IgnoreQueryFilters()
            .Where(r => r.UserId == userId && r.TenantId == tenantId && r.At >= lowest && r.At <= highest)
            .Select(r => r.At)
            .ToListAsync(ct));

        var keptReadings = readings
            .Where(r => instants.GetViewBetween(r.At - spacing + TimeSpan.FromTicks(1), r.At + spacing - TimeSpan.FromTicks(1)).Count == 0
                        && instants.Add(r.At))
            .ToList();
        var keptMeans = means
            .Where(m => instants.GetViewBetween(m.Start, m.End - TimeSpan.FromTicks(1)).Count == 0
                        && instants.GetViewBetween(m.Start - spacing + TimeSpan.FromTicks(1), m.Start + spacing - TimeSpan.FromTicks(1)).Count == 0
                        && instants.Add(m.Start))
            .ToList();

        db.GlucoseReadings.AddRange(keptReadings
            .Select(r => Row(request.Person, r.At, r.MgPerDl, GlucoseSource.Import))
            .Concat(keptMeans.Select(m => Row(request.Person, m.Start, m.MgPerDl, GlucoseSource.ImportHourly))));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new GlucoseImportReport(
            keptReadings.Count,
            keptMeans.Count,
            readings.Count - keptReadings.Count + (means.Count - keptMeans.Count),
            values.Count - readings.Count);
    }

    // Windows of ten days from the span's start to now, read one after another so the home is asked
    // one thing at a time. Each answer opens with the state the sensor had at the window's start,
    // stamped there: that is no measurement (a sensor silent across the start would grow a reading on
    // it), so only what changed after the start is kept.
    private static async Task<IReadOnlyList<HaStateChange>> HistoryAsync(
        IHomeAssistantClient home, string entity, DateTimeOffset from, DateTimeOffset now, CancellationToken ct)
    {
        var starts = Enumerable.Range(0, (int)Math.Ceiling((now - from) / Window))
            .Select(i => from + i * Window)
            .ToList();
        List<HaStateChange> changes = [];
        foreach (var start in starts)
        {
            var end = start + Window < now ? start + Window : now;
            var window = await home.ListHistoryAsync(entity, Stamp(start), Stamp(end), ct);
            changes.AddRange(window.Where(change => change.At > start && change.At <= end));
        }

        return changes
            .Select(change => change with { At = change.At.ToUniversalTime() })
            .DistinctBy(change => change.At)
            .OrderBy(change => change.At)
            .ToList();
    }

    // Home Assistant states an outage as a state of its own (unavailable, unknown); only a positive,
    // finite number is a reading.
    private static double? Reading(string state) =>
        double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Reading(value) : null;

    private static double? Reading(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? v : null;

    // The sensor's own unit, never a guess: a value in mmol/L kept as mg/dL would be a hypo eighteen
    // times over.
    private static double MgPerDlFactor(string entity, HaEntityState state)
    {
        var unit = state.Attributes.TryGetValue("unit_of_measurement", out var node)
                   && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text.Trim()
            : null;
        return unit switch
        {
            _ when string.Equals(unit, "mg/dL", StringComparison.OrdinalIgnoreCase) => 1,
            _ when string.Equals(unit, "mmol/L", StringComparison.OrdinalIgnoreCase) => MgPerDlPerMmol,
            null => throw new ImportException($"{entity} names no unit_of_measurement, so its values could be mg/dL or mmol/L."),
            _ => throw new ImportException($"{entity} is measured in '{unit}', neither mg/dL nor mmol/L.")
        };
    }

    private static GlucoseReading Row(Account person, DateTimeOffset at, double mgPerDl, GlucoseSource source) => new()
    {
        UserId = person.UserId,
        TenantId = person.TenantId,
        At = at.ToUniversalTime(),
        MgPerDl = mgPerDl,
        Trend = GlucoseTrend.Unknown,
        Source = source
    };

    // A window crosses as a UTC instant with its Z: Home Assistant reads a naive one in its own zone.
    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ToTheSecond(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
}