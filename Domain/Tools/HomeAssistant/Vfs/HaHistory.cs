using System.Globalization;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;

namespace Domain.Tools.HomeAssistant.Vfs;

// Implements `history.sh` (see HaHistoryActions): resolves the window, reads the recorder through
// the client, and answers either every change or one summary per bucket.
//
// The window's strings cross to Home Assistant as written (HaDateTimeText). The instants that come
// back are Home Assistant's own, offset included, and are rendered with that offset rather than
// converted: this process does not know the home's zone, and the prompt tells the model to say
// them in the user's.
internal static class HaHistory
{
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IHomeAssistantClient client, string entityId, JsonObject data, TimeProvider time, CancellationToken ct)
    {
        try
        {
            var (start, end) = HaWindow.Resolve(data, time, "hours", TimeSpan.FromHours, HaHistoryActions.DefaultHours);
            var every = HaWindow.WholeNumber(data, "every");
            var limit = HaWindow.WholeNumber(data, "limit") ?? HaHistoryActions.DefaultLimit;

            var changes = await client.ListHistoryAsync(entityId, start, end, ct);

            var payload = new JsonObject
            {
                ["ok"] = true,
                ["entity_id"] = entityId,
                ["window"] = new JsonObject { ["start"] = start, ["end"] = end }
            };

            if (every is { } minutes)
            {
                Summarise(payload, changes, minutes);
            }
            else
            {
                List(payload, changes, limit);
            }

            if (changes.Count == 0)
            {
                payload["suggestion"] =
                    "No recorded changes in this window. Home Assistant keeps history only for its "
                    + "recorder's retention (10 days by default): widen the window with --hours, or check the dates.";
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

    // Buckets are aligned to the clock (multiples of the bucket length since the epoch), so an
    // hourly summary starts on the hour whatever the window's start. A state that is not a number
    // (`unavailable`, `unknown`, a light's `on`) is skipped and counted, and a history with numbers
    // in it at all is an argument error rather than an empty answer.
    private static void Summarise(JsonObject payload, IReadOnlyList<HaStateChange> changes, int minutes)
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
            .GroupBy(s => Math.DivRem(s.Change.At.ToUnixTimeSeconds(), bucketSeconds, out var rem) - (rem < 0 ? 1 : 0))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(s => s.Change.At).ToList();
                var values = ordered.Select(s => s.Value).ToList();
                return (JsonNode?)new JsonObject
                {
                    ["at"] = Stamp(DateTimeOffset.FromUnixTimeSeconds(g.Key * bucketSeconds)),
                    ["min"] = values.Min(),
                    ["max"] = values.Max(),
                    ["mean"] = Math.Round(values.Average(), 1),
                    ["last"] = values[^1],
                    ["samples"] = values.Count
                };
            })
            .ToArray();

        payload["every_minutes"] = minutes;
        payload["samples"] = samples.Count;
        payload["skipped"] = changes.Count - samples.Count;
        payload["buckets"] = new JsonArray(buckets);
    }

    private static string Stamp(DateTimeOffset at) =>
        at.ToString(HaDateTimeText.Format, CultureInfo.InvariantCulture);
}