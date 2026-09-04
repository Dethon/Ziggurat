using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// Configurable in-memory IHomeAssistantClient. `AreaTemplateJson` is the exact JSON the real
// RenderTemplateAsync returns for the area template HaCatalogProvider sends.
public class FakeHaClient : IHomeAssistantClient
{
    public List<HaEntityState> States { get; init; } = [];
    public List<HaServiceDefinition> Services { get; init; } = [];
    public string AreaTemplateJson { get; set; } = """{"areas":[]}""";

    public (string Domain, string Service, string? EntityId, IReadOnlyDictionary<string, JsonNode?>? Data)? LastCall { get; private set; }
    public Func<string, string, string?, IReadOnlyDictionary<string, JsonNode?>?, HaServiceCallResult>? CallHandler { get; set; }

    public virtual Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HaEntityState>>(States);

    public virtual Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
        => Task.FromResult(States.FirstOrDefault(s => s.EntityId == entityId));

    public virtual Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HaServiceDefinition>>(Services);

    public virtual Task<HaServiceCallResult> CallServiceAsync(
        string domain, string service, string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
    {
        LastCall = (domain, service, entityId, data);
        var result = CallHandler?.Invoke(domain, service, entityId, data)
                     ?? new HaServiceCallResult { ChangedEntities = [] };
        return Task.FromResult(result);
    }

    public virtual Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
        => Task.FromResult(AreaTemplateJson);

    // The calendar side: what the listing answers, and what was created or deleted through it.
    public List<HaCalendarEvent> CalendarEvents { get; init; } = [];
    public (string EntityId, string Start, string End)? LastCalendarWindow { get; private set; }
    public List<(string EntityId, HaCalendarEventDraft Draft)> CreatedEvents { get; } = [];
    public List<(string EntityId, string Uid, string? RecurrenceId, string? RecurrenceRange)> DeletedEvents { get; } = [];
    public Exception? CalendarFailure { get; set; }

    public virtual Task<IReadOnlyList<HaCalendarEvent>> ListCalendarEventsAsync(
        string entityId, string start, string end, CancellationToken ct = default)
    {
        LastCalendarWindow = (entityId, start, end);
        return CalendarFailure is null
            ? Task.FromResult<IReadOnlyList<HaCalendarEvent>>(CalendarEvents)
            : Task.FromException<IReadOnlyList<HaCalendarEvent>>(CalendarFailure);
    }

    public virtual Task CreateCalendarEventAsync(string entityId, HaCalendarEventDraft draft, CancellationToken ct = default)
    {
        if (CalendarFailure is not null)
        {
            return Task.FromException(CalendarFailure);
        }
        CreatedEvents.Add((entityId, draft));
        return Task.CompletedTask;
    }

    public virtual Task DeleteCalendarEventAsync(
        string entityId, string uid, string? recurrenceId = null, string? recurrenceRange = null,
        CancellationToken ct = default)
    {
        if (CalendarFailure is not null)
        {
            return Task.FromException(CalendarFailure);
        }
        DeletedEvents.Add((entityId, uid, recurrenceId, recurrenceRange));
        return Task.CompletedTask;
    }

    // The recorder side: what the history read answers, and the window it was asked for.
    public List<HaStateChange> History { get; init; } = [];
    public (string EntityId, string Start, string End)? LastHistoryWindow { get; private set; }
    public Exception? HistoryFailure { get; set; }

    public virtual Task<IReadOnlyList<HaStateChange>> ListHistoryAsync(
        string entityId, string start, string end, CancellationToken ct = default)
    {
        LastHistoryWindow = (entityId, start, end);
        return HistoryFailure is null
            ? Task.FromResult<IReadOnlyList<HaStateChange>>(History)
            : Task.FromException<IReadOnlyList<HaStateChange>>(HistoryFailure);
    }

    public List<HaStatisticsRow> Statistics { get; init; } = [];
    public (string EntityId, string Start, string End, string Period)? LastStatisticsWindow { get; private set; }
    public Exception? StatisticsFailure { get; set; }

    public virtual Task<IReadOnlyList<HaStatisticsRow>> ListStatisticsAsync(
        string entityId, string start, string end, string period, CancellationToken ct = default)
    {
        LastStatisticsWindow = (entityId, start, end, period);
        return StatisticsFailure is null
            ? Task.FromResult<IReadOnlyList<HaStatisticsRow>>(Statistics)
            : Task.FromException<IReadOnlyList<HaStatisticsRow>>(StatisticsFailure);
    }

    public static HaEntityState Entity(string id, string state, params (string Key, JsonNode? Value)[] attrs) => new()
    {
        EntityId = id,
        State = state,
        Attributes = attrs.ToDictionary(a => a.Key, a => a.Value),
        LastChanged = DateTimeOffset.Parse("2026-05-23T09:14:02Z"),
        LastUpdated = DateTimeOffset.Parse("2026-05-23T09:14:02Z")
    };

    public static HaServiceDefinition Service(string domain, string service, JsonNode? target, params (string Name, HaServiceField Field)[] fields) => new()
    {
        Domain = domain,
        Service = service,
        Target = target,
        Fields = fields.ToDictionary(f => f.Name, f => f.Field)
    };

    public static JsonNode AnyEntityTarget() => JsonNode.Parse("""{"entity":[{}]}""")!;
    public static JsonNode DomainTarget(string domain) => JsonNode.Parse($$"""{"entity":[{"domain":["{{domain}}"]}]}""")!;
}