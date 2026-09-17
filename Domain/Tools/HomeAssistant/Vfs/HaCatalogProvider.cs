using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Domain.Tools.HomeAssistant.Vfs;

// Cached source of truth for both the VFS engine and the slim index prompt. Registered as a
// singleton, so the cache is process-wide and shared across every agent session connected to this
// MCP server — correct because HA models one physical home, not per-session state. Caches a
// successful build for `_cacheTtl` (even when HA legitimately has no entities); only an HA *failure*
// falls back to HaCatalog.Empty with a short negative TTL, so a transient outage doesn't blind the
// agent for the full window. Func<IHomeAssistantClient> (not a direct injection) keeps the transient,
// IHttpClientFactory-managed client from being pinned for this singleton's lifetime.
// `extraServices` are action definitions the VFS serves itself (see HaMusicActions and
// HaCalendarActions) rather than forwarding to HA. They join the catalog so glob/read/info/exec
// resolve them through the same paths as real services; only the exec call itself is intercepted.
// One named like a service HA publishes replaces it — the calendar's create_event is served here
// because HA's own takes no recurrence rule — so an action file resolves to exactly one definition.
public sealed class HaCatalogProvider(
    Func<IHomeAssistantClient> clientFactory,
    TimeProvider? timeProvider = null,
    IReadOnlyList<HaServiceDefinition>? extraServices = null,
    ILogger<HaCatalogProvider>? logger = null)
{
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _failureCacheTtl = TimeSpan.FromSeconds(30);

    // Single template render returns one JSON object covering every area and its entities —
    // the REST API has no other path into the area registry. The same render lists the entities
    // the registry hides: the states endpoint serves them regardless, and hidden in the home means
    // hidden from the agent too.
    private const string AreaTemplate =
        """{"areas":[{% for aid in areas() %}{% if not loop.first %},{% endif %}{"id":{{aid|tojson}},"name":{{area_name(aid)|tojson}},"entities":{{area_entities(aid)|list|tojson}}}{% endfor %}],"hidden":{{states|map(attribute='entity_id')|select('is_hidden_entity')|list|tojson}}}""";

    private const string ActivityListAttribute = "activity_list";
    private const string AndroidTvPlatform = "androidtv_remote";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger<HaCatalogProvider> _logger = logger ?? NullLogger<HaCatalogProvider>.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HaCatalog _cached = HaCatalog.Empty;
    private DateTimeOffset _expiry = DateTimeOffset.MinValue;

    public async Task<HaCatalog> GetAsync(CancellationToken ct)
    {
        if (_time.GetUtcNow() < _expiry)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_time.GetUtcNow() < _expiry)
            {
                return _cached;
            }

            var (catalog, succeeded) = await TryBuildAsync(ct);
            _cached = catalog;
            _expiry = _time.GetUtcNow() + (succeeded ? _cacheTtl : _failureCacheTtl);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(HaCatalog Catalog, bool Succeeded)> TryBuildAsync(CancellationToken ct)
    {
        try
        {
            var client = clientFactory();
            var states = client.ListStatesAsync(ct);
            var services = client.ListServicesAsync(ct);
            var areas = LoadAreasAsync(client, ct);
            var zone = LoadZoneAsync(client, _logger, ct);
            await Task.WhenAll(states, services, areas, zone);
            var allServices = extraServices is null or []
                ? services.Result
                : [.. services.Result.Where(s => !extraServices.Any(e => SameAction(e, s))), .. extraServices];
            var hidden = areas.Result.Hidden;
            var visible = states.Result.Where(e => !hidden.Contains(e.EntityId)).ToList();
            await WithPersistedChoicesAsync(client, visible, _logger, ct);
            return (new HaCatalog(visible, allServices, areas.Result.Areas) { HomeZone = zone.Result }, true);
        }
        // Let cancellation propagate without writing the cache — otherwise a cancelled request would
        // poison the (process-wide) cache with an empty catalog for the negative TTL, blinding
        // subsequent, non-cancelled callers. Only genuine HA failures fall back to Empty.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (HaCatalog.Empty, false);
        }
    }

    // An entity that goes unavailable is served with a name and its features and none of the
    // lists its index line is made of — at the one moment "put it on Bluetooth" needs them. What
    // the states endpoint left blank is put back from where Home Assistant persists it, so nothing
    // here remembers a warmer moment, nothing has to be on, and nothing has to have loaded:
    //
    //   - the entity registry keeps `source_list` and `options` as an entity's capabilities,
    //     current from its last state write, so every unavailable entity serving no choice list
    //     is looked up there in one command and gets exactly the choice lists the registry holds;
    //   - a `remote`'s `activity_list` is no capability, so an Android TV Remote's apps are read
    //     from its config entry's options instead (WithConfiguredAppsAsync) — the entry named by
    //     the same registry entry, never by the integration's live entities: a set that is off
    //     when the home starts leaves its integration unloaded until it is next seen (its setup
    //     raises ConfigEntryNotReady), and a template's `integration_entities` then lists nothing.
    //
    // An available entity is taken at its word, so a home with everything on reads neither; a read
    // that fails leaves the entities as served and the catalog whole, said once in the log.
    private static async Task WithPersistedChoicesAsync(
        IHomeAssistantClient client, List<HaEntityState> entities, ILogger logger, CancellationToken ct)
    {
        var blank = entities
            .Select((entity, index) => (entity, index))
            .Where(e => e.entity.State == "unavailable" && !ServesAnyChoiceList(e.entity))
            .ToList();
        if (blank.Count == 0)
        {
            return;
        }
        IReadOnlyDictionary<string, HaRegistryEntry> registry;
        try
        {
            registry = await client.ListRegistryEntriesAsync([.. blank.Select(e => e.entity.EntityId)], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read the entity registry for {Count} unavailable entities; their index lines list no choices",
                blank.Count);
            return;
        }
        foreach (var (entity, index) in blank)
        {
            if (!registry.TryGetValue(entity.EntityId, out var entry))
            {
                continue;
            }
            var lists = HaCatalog.ChoiceHints
                .Select(h => h.Attribute)
                .Where(a => entry.Capabilities?[a] is JsonArray { Count: > 0 })
                .Select(a => new KeyValuePair<string, JsonNode?>(a, entry.Capabilities![a]!.DeepClone()))
                .ToList();
            if (lists.Count > 0)
            {
                entities[index] = WithAttributes(entity, lists);
            }
            else if (entry.Platform == AndroidTvPlatform && entry.ConfigEntryId is { } configEntry
                     && HaCatalog.ClassOf(entity.EntityId) == "remote")
            {
                entities[index] = await WithConfiguredAppsAsync(client, entity, configEntry, logger, ct);
            }
        }
    }

    private static bool ServesAnyChoiceList(HaEntityState entity) =>
        HaCatalog.ChoiceHints.Any(h => entity.Attributes.ContainsKey(h.Attribute));

    private static HaEntityState WithAttributes(HaEntityState entity, IEnumerable<KeyValuePair<string, JsonNode?>> added) =>
        entity with
        {
            Attributes = entity.Attributes.Concat(added).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
        };

    // The living-room TV goes `unavailable` seconds into standby, and the states endpoint then
    // serves its remote with a name and its features and no `activity_list`. Home Assistant keeps
    // the apps in the Android TV Remote entry's options, which the options flow serves whether or
    // not the entry is loaded, so the remote gets the list its entry is configured with.
    private static async Task<HaEntityState> WithConfiguredAppsAsync(
        IHomeAssistantClient client, HaEntityState entity, string configEntry, ILogger logger, CancellationToken ct)
    {
        try
        {
            var apps = await client.ListAndroidTvAppsAsync(configEntry, ct);
            return apps.Count == 0
                ? entity
                : WithAttributes(entity, [new(ActivityListAttribute, new JsonArray([.. apps.Select(app => (JsonNode)app)]))]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read the apps {Entity} is configured with (entry {Entry}); its index line lists none",
                entity.EntityId, configEntry);
            return entity;
        }
    }

    private static bool SameAction(HaServiceDefinition a, HaServiceDefinition b) =>
        a.Domain.Equals(b.Domain, StringComparison.Ordinal) && a.Service.Equals(b.Service, StringComparison.Ordinal);

    // The home's zone is a convenience for one summary, not the catalog's substance: a config read
    // that fails, or an id this runtime's tz database lacks, leaves it null and the catalog whole.
    // Said in the log, though: otherwise the only trace is `bucket_zone: UTC` in a summary's payload.
    private static async Task<TimeZoneInfo?> LoadZoneAsync(
        IHomeAssistantClient client, ILogger logger, CancellationToken ct)
    {
        string? id = null;
        try
        {
            id = await client.GetTimeZoneAsync(ct);
            if (id is null)
            {
                logger.LogWarning("Home Assistant's config names no time zone; history buckets will align to UTC");
                return null;
            }
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not resolve the home's time zone {Zone}; history buckets will align to UTC", id ?? "(unread)");
            return null;
        }
    }

    private sealed record Registry(IReadOnlyList<HaAreaEntities> Areas, IReadOnlySet<string> Hidden)
    {
        public static Registry Empty { get; } = new([], new HashSet<string>(StringComparer.Ordinal));
    }

    private static async Task<Registry> LoadAreasAsync(IHomeAssistantClient client, CancellationToken ct)
    {
        var rendered = await client.RenderTemplateAsync(AreaTemplate, ct);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return Registry.Empty;
        }
        try
        {
            var payload = JsonSerializer.Deserialize<AreaPayload>(rendered);
            var areas = payload?.Areas?
                .Select(a => new HaAreaEntities(a.Id, a.Name, a.Entities ?? []))
                .ToList() ?? [];
            return new Registry(areas, (payload?.Hidden ?? []).ToHashSet(StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return Registry.Empty;
        }
    }

    private sealed record AreaPayload
    {
        [JsonPropertyName("areas")] public IReadOnlyList<AreaDto>? Areas { get; init; }
        [JsonPropertyName("hidden")] public IReadOnlyList<string>? Hidden { get; init; }
    }

    private sealed record AreaDto
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("entities")] public IReadOnlyList<string>? Entities { get; init; }
    }
}