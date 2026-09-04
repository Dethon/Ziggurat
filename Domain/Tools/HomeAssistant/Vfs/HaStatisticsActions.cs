using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// Long-term statistics as an action file: hourly mean/min/max (or sum and change for a total) that
// Home Assistant compiles for every sensor with a state_class and keeps for good — the read that
// reaches past the recorder's retention. They are a WebSocket command and nothing else, so the
// mount serves this (HaStatistics), and only in the directories of the entities that have them:
// an every-entity action narrowed to the ones whose state carries `state_class`.
public static class HaStatisticsActions
{
    public const string Domain = "homeassistant";
    public const string Service = "statistics";
    public const string StateClassAttribute = "state_class";

    public const int DefaultDays = 7;
    public const int DefaultLimit = 500;
    public const string DefaultPeriod = "hour";

    public static readonly IReadOnlyList<string> Periods = ["5minute", "hour", "day", "week", "month"];

    public static HaServiceDefinition Statistics { get; } = new()
    {
        Domain = Domain,
        Service = Service,
        AppliesToEveryEntity = true,
        RequiresAttribute = StateClassAttribute,
        Description =
            "Long-term statistics of this sensor: mean/min/max per period for a measurement, sum and "
            + "change for a total. Home Assistant compiles them hourly and keeps them for good, so they "
            + $"reach past history.sh's retention. With no arguments, the last {DefaultDays} days by the hour.",
        Fields = new Dictionary<string, HaServiceField>
        {
            ["days"] = new()
            {
                Description = $"Window length ending now, or ending at --end_date_time (default {DefaultDays}).",
                Selector = JsonNode.Parse("""{"number":{"min":1,"max":3660}}""")
            },
            ["start_date_time"] = new() { Description = "Window start as a local date-time, e.g. \"2026-08-01 00:00:00\" (an explicit offset is honoured). Not with --days." },
            ["end_date_time"] = new() { Description = $"Window end as a local date-time; defaults to now. Alone, --days (default {DefaultDays}) is counted back from it." },
            ["period"] = new()
            {
                Description = $"One row per period (default {DefaultPeriod}). 5minute rows last only the recorder's retention; the rest are kept for good.",
                Selector = new JsonObject
                {
                    ["select"] = new JsonObject { ["options"] = new JsonArray(Periods.Select(p => (JsonNode?)p).ToArray()) }
                }
            },
            ["limit"] = new()
            {
                Description = $"Most rows to list; the latest are kept (default {DefaultLimit}).",
                Selector = JsonNode.Parse("""{"number":{"min":1,"max":10000}}""")
            }
        }
    };

    public static bool IsStatistics(HaServiceDefinition svc) =>
        svc.Domain.Equals(Domain, StringComparison.Ordinal)
        && svc.Service.Equals(Service, StringComparison.Ordinal);
}