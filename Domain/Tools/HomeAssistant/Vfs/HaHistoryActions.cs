using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// The recorder's history as an action file, served by the mount in EVERY entity directory — the
// read-only classes most of all, since a sensor is the entity whose past is asked about. Nothing in
// Home Assistant's service catalog reads the recorder; the history endpoint is REST only, so this is
// declared here (AppliesToEveryEntity) and run by HaHistory, the pattern the calendar's listing set.
public static class HaHistoryActions
{
    public const string Domain = "homeassistant";
    public const string Service = "history";

    public const int DefaultHours = 24;
    public const int DefaultLimit = 200;

    public static HaServiceDefinition History { get; } = new()
    {
        Domain = Domain,
        Service = Service,
        AppliesToEveryEntity = true,
        Description =
            "Lists this entity's recorded state changes over a window. The first entry is the state the "
            + "entity had when the window opened, stamped at the window's start; the rest are the changes "
            + "inside it. Home Assistant keeps them only for its recorder's retention (10 days unless this "
            + $"home raised it). With no arguments, the last {DefaultHours} hours ending now, every change "
            + "listed. A value that changes every minute makes a long list: pass --every to get "
            + "min/max/mean/last per bucket instead (numeric states only).",
        Fields = new Dictionary<string, HaServiceField>
        {
            ["hours"] = new()
            {
                Description = $"Window length ending now, or ending at --end_date_time (default {DefaultHours}). No ceiling: the recorder's retention is the only bound, and it may have been raised.",
                Selector = JsonNode.Parse("""{"number":{"min":1}}""")
            },
            ["start_date_time"] = new() { Description = "Window start as a local date-time, e.g. \"2026-09-03 22:00:00\" (an explicit offset is honoured). Not with --hours." },
            ["end_date_time"] = new() { Description = $"Window end as a local date-time; defaults to now. Alone, --hours (default {DefaultHours}) is counted back from it." },
            ["every"] = new()
            {
                Description = "Summarise per bucket of this many minutes: min, max, mean, last and sample count. Numeric states only.",
                Selector = JsonNode.Parse("""{"number":{"min":1,"max":1440}}""")
            },
            ["limit"] = new()
            {
                Description = $"Most changes to list; the latest are kept (default {DefaultLimit}).",
                Selector = JsonNode.Parse("""{"number":{"min":1,"max":5000}}""")
            }
        }
    };

    public static bool IsHistory(HaServiceDefinition svc) =>
        svc.Domain.Equals(Domain, StringComparison.Ordinal)
        && svc.Service.Equals(Service, StringComparison.Ordinal);
}