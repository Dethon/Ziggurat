using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// The calendar's action files, served by the mount instead of forwarded to Home Assistant's
// service catalog — because that catalog cannot drive a calendar: `calendar.get_events` answers
// without the uid an event is addressed by, its `create_event` takes no recurrence rule, and
// deleting an event has no service at all. All three exist on the REST calendars endpoint and the
// WebSocket API, which is where these go (see IHomeAssistantClient). They are declared as ordinary
// HaServiceDefinitions so glob, --help and exec routing treat them like any other action, and they
// REPLACE the catalog's same-named `create_event` and `get_events` (HaCatalogProvider), so a
// calendar directory lists each action once.
public static class HaCalendarActions
{
    public const string Domain = "calendar";
    public const string CreateEventService = "create_event";
    public const string DeleteEventService = "delete_event";
    public const string GetEventsService = "get_events";

    public const int DefaultListingDays = 7;

    private static readonly JsonNode _target = JsonNode.Parse("""{"entity":[{"domain":["calendar"]}]}""")!;

    public static HaServiceDefinition CreateEvent { get; } = new()
    {
        Domain = Domain,
        Service = CreateEventService,
        Description =
            "Adds an event. Give a start as a local date-time (--start_date_time) or a date for an "
            + "all-day event (--start_date); the end defaults to one minute (one day) later.",
        Target = _target.DeepClone(),
        Fields = new Dictionary<string, HaServiceField>
        {
            ["summary"] = new() { Required = true, Description = "The event's title — for an alarm, the spoken message." },
            ["start_date_time"] = new() { Description = "Local wall-clock start, e.g. \"2026-06-19 21:30:00\" (an explicit offset is honoured)." },
            ["end_date_time"] = new() { Description = "Local wall-clock end; defaults to one minute after the start." },
            ["start_date"] = new() { Description = "All-day event start, YYYY-MM-DD." },
            ["end_date"] = new() { Description = "All-day event end (exclusive); defaults to the day after the start." },
            ["description"] = new() { Description = "Free text; for an alarm, the JSON with target and insistent." },
            ["location"] = new(),
            ["rrule"] = new() { Description = "RFC 5545 recurrence, e.g. FREQ=DAILY or FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR." }
        }
    };

    public static HaServiceDefinition DeleteEvent { get; } = new()
    {
        Domain = Domain,
        Service = DeleteEventService,
        Description = "Deletes an event by the uid get_events.sh lists it with.",
        Target = _target.DeepClone(),
        Fields = new Dictionary<string, HaServiceField>
        {
            ["uid"] = new() { Required = true, Description = "The event's uid from get_events.sh." },
            ["recurrence_id"] = new() { Description = "For a recurring event: the recurrence_id of the one occurrence to delete." },
            ["recurrence_range"] = new() { Description = "With recurrence_id: THISANDFUTURE deletes that occurrence and every later one." }
        }
    };

    public static HaServiceDefinition GetEvents { get; } = new()
    {
        Domain = Domain,
        Service = GetEventsService,
        Description =
            $"Lists events with the uid each one is deleted by. With no arguments, the {DefaultListingDays} days from now.",
        Target = _target.DeepClone(),
        Fields = new Dictionary<string, HaServiceField>
        {
            ["start_date_time"] = new() { Description = "Window start (local date-time); defaults to now." },
            ["end_date_time"] = new() { Description = "Window end (local date-time)." },
            ["days"] = new()
            {
                Description = $"Window length from the start instead of an end (default {DefaultListingDays}).",
                Selector = JsonNode.Parse("""{"number":{"min":1,"max":366}}""")
            }
        }
    };

    public static IReadOnlyList<HaServiceDefinition> All { get; } = [CreateEvent, DeleteEvent, GetEvents];

    public static bool IsCalendarAction(HaServiceDefinition svc) =>
        svc.Domain.Equals(Domain, StringComparison.Ordinal)
        && All.Any(a => a.Service.Equals(svc.Service, StringComparison.Ordinal));
}