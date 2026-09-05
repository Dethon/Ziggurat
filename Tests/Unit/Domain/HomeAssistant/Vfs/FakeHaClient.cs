using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;

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

    // The seeded entities plus one entity per stored automation, as a real home lists them.
    public virtual Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HaEntityState>>([.. States, .. AutomationEntities()]);

    public virtual Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
        => Task.FromResult(States.Concat(AutomationEntities()).FirstOrDefault(s => s.EntityId == entityId));

    public virtual Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HaServiceDefinition>>(Services);

    public virtual Task<HaServiceCallResult> CallServiceAsync(
        string domain, string service, string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
    {
        LastCall = (domain, service, entityId, data);
        Calls.Add((domain, service, entityId));
        if (domain == "automation" && service is "turn_on" or "turn_off" && entityId is not null)
        {
            var automation = Automations.Values.FirstOrDefault(a => a.EntityId == entityId);
            if (automation is not null)
            {
                automation.IsOn = service == "turn_on";
            }
        }
        var result = CallHandler?.Invoke(domain, service, entityId, data)
                     ?? new HaServiceCallResult { ChangedEntities = [] };
        return Task.FromResult(result);
    }

    public virtual Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
        => Task.FromResult(AreaTemplateJson);

    // The home's configured zone, as `GET /api/config` names it; null when the fake home has none.
    public string? TimeZone { get; set; }
    public Exception? TimeZoneFailure { get; set; }

    public virtual Task<string?> GetTimeZoneAsync(CancellationToken ct = default)
        => TimeZoneFailure is null ? Task.FromResult(TimeZone) : Task.FromException<string?>(TimeZoneFailure);

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

    public List<(string Domain, string Service, string? EntityId)> Calls { get; } = [];

    // The automation store: the configs the config API holds, keyed by id, each with the entity
    // state a real home would list beside it. What was written and deleted is recorded so a test
    // can assert on the automation the home received rather than on how it was rendered.
    public Dictionary<string, FakeAutomation> Automations { get; } = [];
    public List<(string Id, JsonObject Config)> UpsertedAutomations { get; } = [];
    public List<string> DeletedAutomations { get; } = [];
    public Exception? AutomationRejection { get; set; }

    public sealed class FakeAutomation(string id, JsonObject config)
    {
        public string Id { get; } = id;
        public JsonObject Config { get; set; } = config;
        public string EntityId { get; init; } = "automation." + EntitySlug(config["alias"]?.GetValue<string>() ?? id);
        public bool IsOn { get; set; } = true;
        public DateTimeOffset? LastTriggered { get; set; }
    }

    // Home Assistant derives an automation's entity id from its alias the first time it is loaded.
    private static string EntitySlug(string alias) =>
        new string([.. alias.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);

    // Home Assistant writes the id into the stored config and answers it back on a read.
    public FakeAutomation SeedAutomation(string id, JsonObject config, bool on = true, DateTimeOffset? lastTriggered = null)
    {
        config["id"] = id;
        var automation = new FakeAutomation(id, config) { IsOn = on, LastTriggered = lastTriggered };
        Automations[id] = automation;
        return automation;
    }

    private IEnumerable<HaEntityState> AutomationEntities() =>
        Automations.Values.Select(a => Entity(a.EntityId, a.IsOn ? "on" : "off",
            ("id", JsonValue.Create(a.Id)),
            ("friendly_name", JsonValue.Create(a.Config["alias"]?.GetValue<string>() ?? a.Id)),
            ("last_triggered", a.LastTriggered is { } at ? JsonValue.Create(at.ToString("o")) : null)));

    public int AutomationListings { get; private set; }

    public virtual Task<IReadOnlyList<HaAutomationState>> ListAutomationsAsync(CancellationToken ct = default)
    {
        AutomationListings++;
        return Task.FromResult<IReadOnlyList<HaAutomationState>>(Automations.Values
            .Select(a => new HaAutomationState
            {
                EntityId = a.EntityId,
                ConfigId = a.Id,
                IsOn = a.IsOn,
                FriendlyName = a.Config["alias"]?.GetValue<string>(),
                LastTriggered = a.LastTriggered
            })
            .ToList());
    }

    public virtual Task<JsonObject?> GetAutomationConfigAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Automations.TryGetValue(id, out var a) ? a.Config.DeepClone().AsObject() : null);

    public virtual Task UpsertAutomationConfigAsync(string id, JsonObject config, CancellationToken ct = default)
    {
        if (AutomationRejection is not null)
        {
            return Task.FromException(AutomationRejection);
        }

        var stored = config.DeepClone().AsObject();
        UpsertedAutomations.Add((id, stored));
        if (Automations.TryGetValue(id, out var existing))
        {
            existing.Config = stored;
        }
        else
        {
            Automations[id] = new FakeAutomation(id, stored);
        }
        return Task.CompletedTask;
    }

    public virtual Task DeleteAutomationConfigAsync(string id, CancellationToken ct = default)
    {
        if (!Automations.Remove(id))
        {
            return Task.FromException(new HomeAssistantNotFoundException($"Home Assistant returned 404: no automation {id}"));
        }
        DeletedAutomations.Add(id);
        return Task.CompletedTask;
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