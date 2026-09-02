using System.Text.Json.Nodes;

namespace Tests.Integration.Fixtures;

// The events a faked Home Assistant holds, shared between its REST side (the calendars endpoint
// lists them) and its WebSocket side (create and delete change them), so a scenario that creates
// an alarm and then cancels it is exercising one calendar rather than two fakes' opinions of it.
public sealed class FakeCalendarStore
{
    private readonly Lock _gate = new();
    private readonly List<FakeCalendarEvent> _events = [];
    private int _next;

    public IReadOnlyList<FakeCalendarEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public FakeCalendarEvent Seed(string entityId, string summary, string start, string end, string? description = null, string? rrule = null)
    {
        var uid = $"seed-{Interlocked.Increment(ref _next)}";
        var created = new FakeCalendarEvent(entityId, uid, summary, start, end, description, null, rrule);
        lock (_gate)
        {
            _events.Add(created);
        }
        return created;
    }

    // The WebSocket create schema: dtstart/dtend, summary, optional description/location/rrule.
    public FakeCalendarEvent Create(string entityId, JsonObject @event)
    {
        var uid = $"{Guid.NewGuid():N}";
        var created = new FakeCalendarEvent(
            entityId, uid,
            @event["summary"]!.GetValue<string>(),
            @event["dtstart"]!.GetValue<string>(),
            @event["dtend"]!.GetValue<string>(),
            @event["description"]?.GetValue<string>(),
            @event["location"]?.GetValue<string>(),
            @event["rrule"]?.GetValue<string>());
        lock (_gate)
        {
            _events.Add(created);
        }
        return created;
    }

    public bool Delete(string entityId, string uid)
    {
        lock (_gate)
        {
            return _events.RemoveAll(e => e.EntityId == entityId && e.Uid == uid) > 0;
        }
    }

    public IReadOnlyList<FakeCalendarEvent> For(string entityId)
    {
        lock (_gate)
        {
            return _events.Where(e => e.EntityId == entityId).ToList();
        }
    }
}

public sealed record FakeCalendarEvent(
    string EntityId, string Uid, string Summary, string Start, string End,
    string? Description, string? Location, string? Rrule)
{
    // The shape GET /api/calendars/{entity} answers with: an instant is {"dateTime"} or, for a bare
    // date, {"date"}; absent text fields are null rather than missing.
    public JsonNode ToApiJson() => new JsonObject
    {
        ["start"] = Instant(Start),
        ["end"] = Instant(End),
        ["summary"] = Summary,
        ["description"] = Description,
        ["location"] = Location,
        ["uid"] = Uid,
        ["recurrence_id"] = null,
        ["rrule"] = Rrule
    };

    private static JsonNode Instant(string value) =>
        value.Length == 10
            ? new JsonObject { ["date"] = value }
            : new JsonObject { ["dateTime"] = value };
}