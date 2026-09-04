using System.Text.Json.Nodes;
using JetBrains.Annotations;

namespace Domain.Contracts;

public interface IHomeAssistantClient
{
    Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default);
    Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default);
    Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default);
    Task<HaServiceCallResult> CallServiceAsync(
        string domain,
        string service,
        string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data,
        CancellationToken ct = default);

    // Renders a Jinja template via POST /api/template.
    // The only REST path into HA's area/device/entity registries — `{{ areas() }}`,
    // `{{ area_entities(area_id) }}`, `{{ device_id(entity_id) }}`, etc. — so we use
    // it to enumerate setup information that's otherwise WebSocket-only.
    Task<string> RenderTemplateAsync(string template, CancellationToken ct = default);

    // The calendar is the one entity class the service catalog cannot fully drive: `get_events`
    // answers without the uid an event is addressed by, and deleting has no service at all — both
    // exist only on the REST calendars endpoint and the WebSocket API. `start` and `end` are the
    // caller's own date-time strings: Home Assistant reads a naive one in its own time zone, and
    // converting here would need a zone this side does not know.
    Task<IReadOnlyList<HaCalendarEvent>> ListCalendarEventsAsync(
        string entityId, string start, string end, CancellationToken ct = default);

    Task CreateCalendarEventAsync(string entityId, HaCalendarEventDraft draft, CancellationToken ct = default);

    Task DeleteCalendarEventAsync(
        string entityId, string uid, string? recurrenceId = null, string? recurrenceRange = null,
        CancellationToken ct = default);

    // The recorder's one read: `GET /api/history/period/{start}` asked minimally (state and instant
    // only, no attributes), which answers the entity's state at the window's start followed by every
    // change inside it. Nothing in the service catalog answers "what was this over the last day".
    // `start` and `end` cross as the caller's strings for the same reason the calendar's do.
    Task<IReadOnlyList<HaStateChange>> ListHistoryAsync(
        string entityId, string start, string end, CancellationToken ct = default);
}

// One recorded state and the instant it was taken, as the history endpoint lists them.
[PublicAPI]
public record HaStateChange
{
    public required string State { get; init; }
    public required DateTimeOffset At { get; init; }
}

// One event as the calendars endpoint lists it. `Start`/`End` are the strings Home Assistant sent —
// a local date-time with its offset, or a bare date when `AllDay`.
[PublicAPI]
public record HaCalendarEvent
{
    public required string Uid { get; init; }
    public required string Summary { get; init; }
    public required string Start { get; init; }
    public required string End { get; init; }
    public bool AllDay { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    public string? Rrule { get; init; }
    public string? RecurrenceId { get; init; }
}

// What creating an event needs: the WebSocket schema's `dtstart`/`dtend` (a date-time each, or a
// date each for an all-day event) beside the text fields and an optional RFC 5545 recurrence rule.
[PublicAPI]
public record HaCalendarEventDraft
{
    public required string Summary { get; init; }
    public required string Start { get; init; }
    public required string End { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    public string? Rrule { get; init; }
}

[PublicAPI]
public record HaEntityState
{
    public required string EntityId { get; init; }
    public required string State { get; init; }
    public IReadOnlyDictionary<string, JsonNode?> Attributes { get; init; } =
        new Dictionary<string, JsonNode?>();
    public DateTimeOffset? LastChanged { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
}

[PublicAPI]
public record HaServiceDefinition
{
    public required string Domain { get; init; }
    public required string Service { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, HaServiceField> Fields { get; init; } =
        new Dictionary<string, HaServiceField>();

    // Present when the service is entity-targeted. `{}` means "any entity"; a populated
    // shape (e.g. `{entity: [{domain: ["vacuum"]}]}`) narrows acceptable entity kinds.
    // Absent (null) means the service takes no entity target.
    public JsonNode? Target { get; init; }

    // An action the mount serves in EVERY entity directory, read-only classes included, under its
    // bare name — the recorder's history is one. Set only on served definitions; a catalog service
    // never carries it, which is what keeps `homeassistant.turn_on` out of every directory.
    public bool AppliesToEveryEntity { get; init; }
}

[PublicAPI]
public record HaServiceField
{
    public string? Description { get; init; }
    public bool Required { get; init; }
    public JsonNode? Example { get; init; }

    // HA's UI hint for the field type — e.g. {"object":{}}, {"number":{"min":1,"max":99}},
    // {"select":{"multiple":true,"options":[...]}}. The only signal in /api/services that
    // reveals "this field takes a list/object/scalar" when example is absent.
    public JsonNode? Selector { get; init; }
}

[PublicAPI]
public record HaServiceCallResult
{
    public required IReadOnlyList<HaEntityState> ChangedEntities { get; init; }
    public JsonNode? Response { get; init; }
}