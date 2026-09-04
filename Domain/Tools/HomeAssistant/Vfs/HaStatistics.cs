using System.Globalization;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;

namespace Domain.Tools.HomeAssistant.Vfs;

// Implements `statistics.sh` (see HaStatisticsActions): resolves the window, reads the recorder's
// compiled rows through the client, and renders each row with only the measures it carries — a
// measured sensor has mean/min/max, a total has state/sum/change — so a reader is never handed a
// zero that means "not that kind of sensor".
internal static class HaStatistics
{
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IHomeAssistantClient client, string entityId, JsonObject data, TimeProvider time, CancellationToken ct)
    {
        try
        {
            var (start, end) = HaWindow.Resolve(data, time, "days", TimeSpan.FromDays, HaStatisticsActions.DefaultDays);
            var period = HaWindow.Text(data, "period") ?? HaStatisticsActions.DefaultPeriod;
            var limit = HaWindow.WholeNumber(data, "limit") ?? HaStatisticsActions.DefaultLimit;

            var rows = await client.ListStatisticsAsync(entityId, start, end, period, ct);

            var truncated = rows.Count > limit;
            var shown = truncated ? rows.Skip(rows.Count - limit).ToList() : rows;

            var payload = new JsonObject
            {
                ["ok"] = true,
                ["entity_id"] = entityId,
                ["window"] = new JsonObject { ["start"] = start, ["end"] = end },
                ["period"] = period,
                ["rows"] = new JsonArray(shown.Select(Render).ToArray()),
                ["count"] = rows.Count,
                ["truncated"] = truncated
            };

            if (truncated)
            {
                payload["suggestion"] =
                    $"{rows.Count} rows in this window; showing the latest {limit}. Use a coarser --period "
                    + "(day, week, month), raise --limit, or narrow the window.";
            }
            else if (rows.Count == 0)
            {
                payload["suggestion"] =
                    "No statistics in this window. Home Assistant compiles them at twelve past each hour for "
                    + "a sensor with a state_class, so one given a state_class today has its first row within "
                    + "the hour; 5minute rows last only the recorder's retention. For the raw changes, history.sh.";
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

    private static JsonNode? Render(HaStatisticsRow row)
    {
        var node = new JsonObject { ["start"] = Stamp(row.Start), ["end"] = Stamp(row.End) };
        Put(node, "mean", row.Mean);
        Put(node, "min", row.Min);
        Put(node, "max", row.Max);
        Put(node, "state", row.State);
        Put(node, "sum", row.Sum);
        Put(node, "change", row.Change);
        return node;
    }

    private static void Put(JsonObject node, string name, double? value)
    {
        if (value is { } number)
        {
            node[name] = number;
        }
    }

    private static string Stamp(DateTimeOffset at) =>
        at.ToString(HaDateTimeText.Format, CultureInfo.InvariantCulture);
}