using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed record HaAreaEntities(string Id, string Name, IReadOnlyList<string> EntityIds);

public sealed record HaCatalog(
    IReadOnlyList<HaEntityState> Entities,
    IReadOnlyList<HaServiceDefinition> Services,
    IReadOnlyList<HaAreaEntities> Areas)
{
    public const string UnassignedArea = "unassigned";

    // The home's clock, from its configuration; null when unread or unknown to this runtime. The
    // recorder stamps history in UTC, so a summary that opens a day at the home's midnight reads
    // the zone here rather than from the stamps.
    public TimeZoneInfo? HomeZone { get; init; }

    public static HaCatalog Empty { get; } = new([], [], []);

    // The attribute lists that ARE an action's argument: an app to open, a source, an option.
    // The setup index says them on the entity's line with the action that takes them, and the
    // provider keeps them for an entity that has gone unavailable (whose state is then a stub).
    // Only lists that are literally a flag's value belong here — a climate's mode lists would
    // spend many tokens on a call the model already makes without help.
    public static IReadOnlyList<(string Attribute, string Service, string Argument)> ChoiceHints { get; } =
    [
        ("activity_list", "turn_on", "activity"),
        ("source_list", "select_source", "source"),
        ("options", "select_option", "option"),
    ];

    public IReadOnlyList<string> ClassDomains() => Entities
        .Select(e => ClassOf(e.EntityId))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(d => d, StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<string> ObjectIdsFor(string classDomain) => Entities
        .Where(e => ClassOf(e.EntityId).Equals(classDomain, StringComparison.Ordinal))
        .Select(e => ObjectOf(e.EntityId))
        .OrderBy(o => o, StringComparer.Ordinal)
        .ToList();

    public HaEntityState? EntityById(string entityId) =>
        Entities.FirstOrDefault(e => e.EntityId.Equals(entityId, StringComparison.Ordinal));

    public IReadOnlyList<string> AreaSlugs()
    {
        var slugs = Areas
            .Where(a => a.EntityIds.Any(AssignedExists))
            .Select(a => a.Id)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (Unassigned().Count > 0)
        {
            slugs.Add(UnassignedArea);
        }
        return slugs;
    }

    public IReadOnlyList<string> EntityIdsInArea(string area)
    {
        if (area.Equals(UnassignedArea, StringComparison.Ordinal))
        {
            return Unassigned();
        }
        return Areas
            .FirstOrDefault(a => a.Id.Equals(area, StringComparison.Ordinal))?.EntityIds
            .Where(AssignedExists)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList() ?? [];
    }

    private bool AssignedExists(string entityId) => EntityById(entityId) is not null;

    private IReadOnlyList<string> Unassigned()
    {
        var assigned = Areas.SelectMany(a => a.EntityIds).ToHashSet(StringComparer.Ordinal);
        return Entities
            .Select(e => e.EntityId)
            .Where(id => !assigned.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public static string ClassOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot < 0 ? entityId : entityId[..dot];
    }

    public static string ObjectOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot < 0 ? entityId : entityId[(dot + 1)..];
    }

    public static string? FriendlyName(HaEntityState? entity) =>
        entity is not null
        && entity.Attributes.TryGetValue("friendly_name", out var value)
        && value is JsonValue jv
        && jv.TryGetValue<string>(out var name)
            ? name
            : null;
}