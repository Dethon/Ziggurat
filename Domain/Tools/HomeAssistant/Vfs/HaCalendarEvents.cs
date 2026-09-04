using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;

namespace Domain.Tools.HomeAssistant.Vfs;

// Implements the calendar action files (see HaCalendarActions): the listing that carries uids, the
// creation that takes a recurrence rule, and the deletion the service catalog does not have.
//
// Date-times are passed through as the strings the caller wrote. Home Assistant reads a naive one
// in its own time zone, and this process may sit in another, so parsing into an instant here would
// move an alarm by the difference. The one arithmetic done — an end derived from a start, a window
// end derived from its start — keeps the string's own shape: separator, precision and offset.
internal static class HaCalendarEvents
{
    private static readonly TimeSpan _defaultEventLength = TimeSpan.FromMinutes(1);

    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IHomeAssistantClient client, HaServiceDefinition svc, string entityId, JsonObject data,
        TimeProvider time, CancellationToken ct)
    {
        try
        {
            return svc.Service switch
            {
                HaCalendarActions.CreateEventService => await CreateAsync(client, entityId, data, ct),
                HaCalendarActions.DeleteEventService => await DeleteAsync(client, entityId, data, ct),
                HaCalendarActions.GetEventsService => await ListAsync(client, entityId, data, time, ct),
                _ => (127, "", $"command not found: {svc.Service}.sh")
            };
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

    private static async Task<(int, string, string)> CreateAsync(
        IHomeAssistantClient client, string entityId, JsonObject data, CancellationToken ct)
    {
        var summary = Text(data, "summary")
            ?? throw new ArgumentException("Missing required argument '--summary' (the event's title).");
        var (start, end) = Span(data);

        var draft = new HaCalendarEventDraft
        {
            Summary = summary,
            Start = start,
            End = end,
            Description = Text(data, "description"),
            Location = Text(data, "location"),
            Rrule = Text(data, "rrule")
        };
        await client.CreateCalendarEventAsync(entityId, draft, ct);

        var created = new JsonObject { ["summary"] = summary, ["start"] = start, ["end"] = end };
        if (draft.Rrule is not null)
        {
            created["rrule"] = draft.Rrule;
        }
        return (0, new JsonObject { ["ok"] = true, ["created"] = created }.ToJsonString(), "");
    }

    // A date-time start and a date start are two shapes of one event, never both; the end, when
    // given, must be the same shape as the start.
    private static (string Start, string End) Span(JsonObject data)
    {
        var startDateTime = Text(data, "start_date_time");
        var startDate = Text(data, "start_date");
        var endDateTime = Text(data, "end_date_time");
        var endDate = Text(data, "end_date");

        if (startDateTime is not null && startDate is not null)
        {
            throw new ArgumentException("Give either --start_date_time or --start_date, not both.");
        }

        if (startDateTime is not null)
        {
            if (endDate is not null)
            {
                throw new ArgumentException("--end_date goes with --start_date; a --start_date_time takes --end_date_time.");
            }
            return (startDateTime, endDateTime ?? HaDateTimeText.Shift(startDateTime, _defaultEventLength, "start_date_time"));
        }

        if (startDate is not null)
        {
            if (endDateTime is not null)
            {
                throw new ArgumentException("--end_date_time goes with --start_date_time; a --start_date takes --end_date.");
            }
            return (startDate, endDate ?? HaDateTimeText.ShiftDate(startDate, 1, "start_date"));
        }

        throw new ArgumentException(
            "Missing the start: --start_date_time \"YYYY-MM-DD HH:MM:SS\" for a timed event, or --start_date YYYY-MM-DD for an all-day one.");
    }

    private static async Task<(int, string, string)> DeleteAsync(
        IHomeAssistantClient client, string entityId, JsonObject data, CancellationToken ct)
    {
        var uid = Text(data, "uid")
            ?? throw new ArgumentException("Missing required argument '--uid'. List the events with get_events.sh and pass the uid of the one to delete.");

        await client.DeleteCalendarEventAsync(
            entityId, uid, Text(data, "recurrence_id"), Text(data, "recurrence_range"), ct);

        return (0, new JsonObject { ["ok"] = true, ["deleted"] = uid }.ToJsonString(), "");
    }

    private static async Task<(int, string, string)> ListAsync(
        IHomeAssistantClient client, string entityId, JsonObject data, TimeProvider time, CancellationToken ct)
    {
        var start = Text(data, "start_date_time") ?? HaDateTimeText.Now(time);
        var end = Text(data, "end_date_time");
        var days = data["days"]?.GetValue<int>();

        if (end is not null && days is not null)
        {
            throw new ArgumentException("Give either --end_date_time or --days, not both.");
        }
        end ??= HaDateTimeText.Shift(start, TimeSpan.FromDays(days ?? HaCalendarActions.DefaultListingDays), "start_date_time");

        var events = await client.ListCalendarEventsAsync(entityId, start, end, ct);

        var payload = new JsonObject
        {
            ["ok"] = true,
            ["window"] = new JsonObject { ["start"] = start, ["end"] = end },
            ["events"] = new JsonArray(events.Select(Render).ToArray())
        };
        if (events.Count == 0)
        {
            payload["suggestion"] = "No events in this window. Widen it with --days or an explicit --end_date_time.";
        }
        return (0, payload.ToJsonString(), "");
    }

    private static JsonNode Render(HaCalendarEvent e)
    {
        var node = new JsonObject
        {
            ["uid"] = e.Uid,
            ["summary"] = e.Summary,
            ["start"] = e.Start,
            ["end"] = e.End,
            ["allDay"] = e.AllDay
        };
        Put(node, "description", e.Description);
        Put(node, "location", e.Location);
        Put(node, "rrule", e.Rrule);
        Put(node, "recurrence_id", e.RecurrenceId);
        return node;
    }

    private static void Put(JsonObject node, string name, string? value)
    {
        if (value is not null)
        {
            node[name] = value;
        }
    }

    // The parser hands a text field over as a string; a structured value (a JSON description the
    // parser left as an object) is written back out as its JSON.
    private static string? Text(JsonObject data, string name) => data[name] switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text) ? null : text,
        JsonNode node => node.ToJsonString(),
        null => null
    };
}