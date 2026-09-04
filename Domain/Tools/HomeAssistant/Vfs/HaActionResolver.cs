using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaActionResolver
{
    public static IReadOnlyList<HaServiceDefinition> ServicesFor(
        string entityId, IReadOnlyList<HaServiceDefinition> services)
    {
        var classDomain = HaCatalog.ClassOf(entityId);
        return services
            .Where(s => Applies(s, classDomain))
            .OrderBy(s => CommandName(s, classDomain), StringComparer.Ordinal)
            .ToList();
    }

    // The action-file name an entity's directory exposes for a service. A same-domain service keeps
    // its bare service name (`turn_on.sh`); a service from ANOTHER domain that targets this entity
    // class (e.g. `music_assistant.play_media` on a `media_player`) is domain-qualified
    // (`music_assistant.play_media.sh`) so it never collides with a same-named same-domain service
    // (`media_player.play_media` -> `play_media.sh`). exec/read/info resolve back through the same
    // CommandName, so the qualified name round-trips.
    public static string CommandName(HaServiceDefinition svc, string classDomain) =>
        svc.AppliesToEveryEntity || svc.Domain.Equals(classDomain, StringComparison.Ordinal)
            ? svc.Service
            : $"{svc.Domain}.{svc.Service}";

    // A service applies to an entity when it declares itself for every entity (see
    // HaServiceDefinition.AppliesToEveryEntity), or is either:
    //  - a same-domain service that entity-targets this class (the original rule), or
    //  - a cross-domain service whose target EXPLICITLY names this class domain (e.g. Music
    //    Assistant's `music_assistant.play_media` targeting `media_player`). The explicit-name
    //    requirement keeps generic global services (`homeassistant.turn_on`, whose target accepts
    //    ANY entity) from flooding every entity directory.
    private static bool Applies(HaServiceDefinition svc, string classDomain) =>
        svc.AppliesToEveryEntity
        || (svc.Domain.Equals(classDomain, StringComparison.Ordinal)
            ? TargetAcceptsEntity(svc.Target, classDomain)
            : TargetNamesDomain(svc.Target, classDomain));

    // target == null  -> not entity-targeted, exclude.
    // target has no "entity" constraint we can read -> accept (it targets entities generically).
    // target.entity is a list of selector objects; accept if any has no "domain" narrowing,
    // or a "domain" list/string that includes the entity's class.
    private static bool TargetAcceptsEntity(JsonNode? target, string classDomain) =>
        EntitySelectors(target) is { } selectors && selectors.Any(sel => SelectorAcceptsDomain(sel, classDomain));

    // Stricter than TargetAcceptsEntity: a selector with no "domain" narrowing is REJECTED rather
    // than accepted, so a cross-domain service earns a directory slot only by naming the class.
    private static bool TargetNamesDomain(JsonNode? target, string classDomain) =>
        EntitySelectors(target) is { } selectors && selectors.Any(sel => SelectorNamesDomain(sel, classDomain));

    private static IEnumerable<JsonNode?>? EntitySelectors(JsonNode? target)
    {
        if (target is null)
        {
            return null;
        }
        if (target["entity"] is not JsonNode entity)
        {
            // Entity-targeted but with no readable "entity" shape: a single permissive selector.
            return [null];
        }
        return entity as JsonArray ?? (IEnumerable<JsonNode?>)[entity.DeepClone()];
    }

    private static bool SelectorAcceptsDomain(JsonNode? selector, string classDomain) =>
        selector?["domain"] is not JsonNode domain || DomainMatches(domain, classDomain);

    private static bool SelectorNamesDomain(JsonNode? selector, string classDomain) =>
        selector?["domain"] is JsonNode domain && DomainMatches(domain, classDomain);

    private static bool DomainMatches(JsonNode domain, string classDomain) => domain switch
    {
        JsonArray arr => arr.Any(d => d?.GetValue<string>() == classDomain),
        JsonValue v => v.GetValue<string>() == classDomain,
        _ => false
    };
}