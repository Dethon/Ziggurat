using System.Text.Json;
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
    // the REST API has no other path into the area registry.
    private const string AreaTemplate =
        """{"areas":[{% for aid in areas() %}{% if not loop.first %},{% endif %}{"id":{{aid|tojson}},"name":{{area_name(aid)|tojson}},"entities":{{area_entities(aid)|list|tojson}}}{% endfor %}]}""";

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
            return (new HaCatalog(states.Result, allServices, areas.Result) { HomeZone = zone.Result }, true);
        }
        // Let cancellation propagate without writing the cache — otherwise a cancelled request would
        // poison the (process-wide) cache with an empty catalog for the negative TTL, blinding
        // subsequent, non-cancelled callers. Only genuine HA failures fall back to Empty.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (HaCatalog.Empty, false);
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

    private static async Task<IReadOnlyList<HaAreaEntities>> LoadAreasAsync(IHomeAssistantClient client, CancellationToken ct)
    {
        var rendered = await client.RenderTemplateAsync(AreaTemplate, ct);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return [];
        }
        try
        {
            var payload = JsonSerializer.Deserialize<AreaPayload>(rendered);
            return payload?.Areas?
                .Select(a => new HaAreaEntities(a.Id, a.Name, a.Entities ?? []))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record AreaPayload
    {
        [JsonPropertyName("areas")] public IReadOnlyList<AreaDto>? Areas { get; init; }
    }

    private sealed record AreaDto
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("entities")] public IReadOnlyList<string>? Entities { get; init; }
    }
}