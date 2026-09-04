using System.Globalization;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;

namespace Domain.Tools.HomeAssistant.Vfs;

// Implements `history.sh` (see HaHistoryActions): resolves the window, reads the recorder through
// the client, and answers either every change or one summary per bucket.
//
// The window's strings cross to Home Assistant as written (HaDateTimeText). The instants that come
// back are Home Assistant's own — always UTC, whatever the home's zone: the history endpoint formats
// `last_changed` from a UTC timestamp — and a listing renders them as they came; the prompt tells
// the model to say them in the user's time. A summary is the one place the home's clock matters (a
// day bucket should open at the home's midnight), so it takes the zone the catalog read from the
// home's configuration, and buckets on UTC, saying so, when there is none.
internal static class HaHistory
{
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IHomeAssistantClient client, string entityId, JsonObject data, TimeProvider time, TimeZoneInfo? homeZone,
        CancellationToken ct)
    {
        try
        {
            var (start, end) = HaWindow.Resolve(data, time, "hours", TimeSpan.FromHours, HaHistoryActions.DefaultHours);
            var every = HaWindow.WholeNumber(data, "every");
            var limit = HaWindow.WholeNumber(data, "limit");
            if (every is not null && limit is not null)
            {
                throw new ArgumentException(
                    "Give either --every or --limit, not both: --limit caps a listing of changes, and --every "
                    + "answers buckets instead of a listing.");
            }

            var changes = await client.ListHistoryAsync(entityId, start, end, ct);

            var payload = new JsonObject
            {
                ["ok"] = true,
                ["entity_id"] = entityId,
                ["window"] = new JsonObject { ["start"] = start, ["end"] = end }
            };

            if (every is { } minutes)
            {
                Summarise(payload, changes, minutes, homeZone ?? TimeZoneInfo.Utc);
            }
            else
            {
                List(payload, changes, limit ?? HaHistoryActions.DefaultLimit);
            }

            if (changes.Count == 0)
            {
                payload["suggestion"] =
                    "No recorded changes in this window: either nothing changed, or the window is older than the "
                    + "recorder's retention (10 days unless this home raised it; nothing here says which). Widen the "
                    + "window with --hours or check the dates before concluding the past is gone; for a sensor with a "
                    + "state_class, statistics.sh reaches further back.";
            }
            return (0, payload.ToJsonString(), "");
        }
        catch (ArgumentException ex)
        {
            return (2, "", ex.Message);
        }
        catch (HomeAssistantException ex)
        {
            return (1, "", ex.Message);
        }
    }

    private static void List(JsonObject payload, IReadOnlyList<HaStateChange> changes, int limit)
    {
        var truncated = changes.Count > limit;
        var shown = truncated ? changes.Skip(changes.Count - limit).ToList() : changes;

        payload["changes"] = new JsonArray(shown
            .Select(c => (JsonNode?)new JsonObject { ["at"] = Stamp(c.At), ["state"] = c.State })
            .ToArray());
        payload["count"] = changes.Count;
        payload["truncated"] = truncated;

        if (truncated)
        {
            payload["suggestion"] =
                $"{changes.Count} changes in this window; showing the latest {limit}. Summarise with "
                + "--every <minutes> (min/max/mean per bucket, numeric states), raise --limit, or narrow the window.";
        }
    }

    // Buckets are aligned to the home's clock (multiples of the bucket length since the epoch, as
    // that zone counts wall-clock seconds), so an hourly summary starts on the hour and a daily one
    // at the home's midnight, whatever the window's start and whatever offset the stamps carry — the
    // recorder's are always UTC. A bucket's stamp carries the home's offset at the instant it opens.
    // A state that is not a number (`unavailable`, `unknown`, a light's `on`) is skipped and counted,
    // and a history with no number in it at all is an argument error rather than an empty answer.
    private static void Summarise(JsonObject payload, IReadOnlyList<HaStateChange> changes, int minutes, TimeZoneInfo zone)
    {
        var samples = changes
            .Select(c => (Change: c, Ok: double.TryParse(c.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var value), Value: value))
            .Where(s => s.Ok)
            .ToList();

        if (changes.Count > 0 && samples.Count == 0)
        {
            throw new ArgumentException(
                "--every summarises numeric states only, and none of this entity's recorded states is a number. "
                + "Drop --every and read the changes themselves (--limit caps how many).");
        }

        var bucketSeconds = minutes * 60L;
        var buckets = samples
            .GroupBy(s => Bucket(s.Change.At, bucketSeconds, zone))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(s => s.Change.At).ToList();
                var values = ordered.Select(s => s.Value).ToList();
                return (JsonNode?)new JsonObject
                {
                    ["at"] = Stamp(BucketStart(g.Key, bucketSeconds, zone)),
                    ["min"] = values.Min(),
                    ["max"] = values.Max(),
                    ["mean"] = Math.Round(values.Average(), 1),
                    ["last"] = values[^1],
                    ["samples"] = values.Count
                };
            })
            .ToArray();

        payload["every_minutes"] = minutes;
        payload["bucket_zone"] = zone.Id;
        payload["samples"] = samples.Count;
        payload["skipped"] = changes.Count - samples.Count;
        payload["buckets"] = new JsonArray(buckets);
    }

    // The bucket index of an instant on the zone's clock: wall-clock seconds since the epoch as the
    // zone counts them at that instant, floored to a multiple of the bucket. Two instants share a
    // bucket exactly when the home's wall clock puts them in the same one.
    private static long Bucket(DateTimeOffset at, long bucketSeconds, TimeZoneInfo zone)
    {
        var wallClockSeconds = at.ToUnixTimeSeconds() + (long)zone.GetUtcOffset(at).TotalSeconds;
        return Math.DivRem(wallClockSeconds, bucketSeconds, out var rem) - (rem < 0 ? 1 : 0);
    }

    // The instant a bucket opens on the zone's clock: the wall-clock second it starts at, read back
    // through the offset the zone has at that moment (a first guess with the offset of the wall
    // clock taken as UTC, corrected once for the zone's offset there — exact outside the hour a
    // DST change skips or repeats, and never off by more than that hour inside it).
    private static DateTimeOffset BucketStart(long index, long bucketSeconds, TimeZoneInfo zone)
    {
        var wallClock = DateTimeOffset.FromUnixTimeSeconds(index * bucketSeconds);
        var offset = zone.GetUtcOffset(wallClock - zone.GetUtcOffset(wallClock));
        return new DateTimeOffset(wallClock.DateTime, offset);
    }

    private static string Stamp(DateTimeOffset at) =>
        at.ToString(HaDateTimeText.Format, CultureInfo.InvariantCulture);
}