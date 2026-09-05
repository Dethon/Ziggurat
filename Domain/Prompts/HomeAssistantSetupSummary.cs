using System.Text;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;

namespace Domain.Prompts;

// Builds the directory dump appended to HomeAssistantPrompt at MCP-prompt-fetch time. Backed by
// the shared HaCatalogProvider cache. Returns "" when the catalog is empty so the caller falls
// back to the static prompt alone.
//
// Each entity is listed ONCE, under its room, as the bare composed segment. It used to be listed
// twice — once per tree — with the full path repeated on every line, which on the live house was
// 26.5k characters of a ~28k-token prefix; one tree grouped by room is 10.7k for the same reach.
// Nothing shrinks in what resolves: both path forms still address the entity, and the header
// carries the rule for rebuilding either one. The composed `<id>_(<slug>)` segment stays verbatim
// because HaFileSystem.ResolveEntity is strict-canonical — a bare id yields a hint, not a hit,
// so shortening the segment itself would buy characters at the price of a round trip.
public class HomeAssistantSetupSummary(
    HaCatalogProvider catalogProvider, HaWatches? watches = null, ISatelliteCatalog? satellites = null)
{
    private const string SetupHeader =
        "Mounted at `/ha`. Every entity is listed once below, under its room, as the exact "
        + "directory segment — use it verbatim. The full path is `/ha/areas/<room>/<entry>`. The "
        + "same entity is also at `/ha/entities/<class>/<object-id>_(<slug>)`, where `<class>` is "
        + "the entry up to its first `.` and `<object-id>_(<slug>)` is the rest.";

    private const string ActionsHeader =
        "Action files live in the ENTITY directory (`/ha/entities/<class>/<id>/<action>.sh`), "
        + "never in the class directory — `glob` on `/ha/entities/<class>/*.sh` always returns "
        + "nothing. Use this table instead of globbing to discover actions. The `every entity` "
        + "lines name the actions every directory has, read-only classes included (an `every "
        + "entity with <attribute>` line: every entity whose state.json carries that attribute); "
        + "a class absent from the rest has only those. If one entity lacks a listed action, "
        + "`exec` returns exitCode 127 and `stderr` names the ones it does have.";

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        if (catalog.Entities.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("## Current Home Assistant setup\n\n");
        sb.Append(SetupHeader).Append("\n\n");
        sb.Append(string.Join("\n", BuildRoomSections(catalog)));

        var everywhere = EveryEntityActions(catalog);
        var actions = BuildActionTable(catalog);
        if (everywhere.Count > 0 || actions.Count > 0)
        {
            sb.Append("\n## Actions by entity class\n\n");
            sb.Append(ActionsHeader).Append("\n\n");
            sb.Append(string.Concat(everywhere.Select(line => line + "\n")));
            sb.Append(string.Join("\n", actions)).Append('\n');
        }

        if (await WatchesLineAsync(ct) is { } watchesLine)
        {
            sb.Append('\n').Append(watchesLine).Append('\n');
        }

        if (await SatellitesLineAsync(ct) is { } satellitesLine)
        {
            sb.Append(satellitesLine).Append('\n');
        }

        return sb.ToString();
    }

    // The rooms an announcement can reach are the voice hub's, spelled its way, and the first prod
    // watch reached for a Home Assistant area slug instead. Read live from the hub, the roster the
    // timers prompt shows; a hub that cannot answer leaves the line out rather than listing nothing.
    private async Task<string?> SatellitesLineAsync(CancellationToken ct)
    {
        if (satellites is null)
        {
            return null;
        }

        try
        {
            var roster = await satellites.GetAllAsync(ct);
            return roster.Count == 0
                ? null
                : "voice satellites: " + string.Join(", ", roster.Select(s => $"{s.Id} (room \"{s.Room}\")"))
                  + " — an announce target is one of these rooms or ids, never a Home Assistant area.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // The watches exist and where they are, so the agent discovers the feature without being told.
    // The count is read live from the home; a home that cannot answer for them says nothing rather
    // than listing an empty subtree as fact.
    private async Task<string?> WatchesLineAsync(CancellationToken ct)
    {
        if (watches is null)
        {
            return null;
        }

        try
        {
            var existing = await watches.ListAsync(ct);
            var listing = existing.Count == 0
                ? "none yet"
                : string.Join(", ", existing.Select(w => w.Spent ? $"`{w.Id}` (spent)" : w.Enabled ? $"`{w.Id}`" : $"`{w.Id}` (paused)"));
            return $"watches: `/ha/watches/<id>/watch.json` — {existing.Count} defined ({listing}); see the guide's Watches section.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // AreaSlugs() already orders real rooms ordinally and appends `unassigned` last, and
    // EntityIdsInArea() orders within a room — the composed segment keeps that order because it
    // only ever appends to the id. So neither is re-sorted here.
    private static IEnumerable<string> BuildRoomSections(HaCatalog catalog) =>
        catalog.AreaSlugs()
            .Select(area => (area, entries: BuildRoomEntries(catalog, area)))
            .Where(section => section.entries.Count > 0)
            .Select(section =>
                $"### {section.area}\n{string.Join("\n", section.entries)}\n");

    private static IReadOnlyList<string> BuildRoomEntries(HaCatalog catalog, string area) =>
        catalog.EntityIdsInArea(area)
            .Select(entityId =>
                HaSlug.Compose(entityId, HaCatalog.FriendlyName(catalog.EntityById(entityId))))
            .ToList();

    // Grouped by class, not per entity: every entity of a class exposes the same actions, so the
    // per-entity form costs ~4.4k tokens to say what ~350 says. That size difference is the whole
    // point — a round trip costs ~1.15s, prompt prefill ~0.05ms/token, so buying a turn back with
    // tokens only works while the tokens stay cheap.
    private static IReadOnlyList<string> BuildActionTable(HaCatalog catalog) =>
        catalog.ClassDomains()
            .Select(classDomain => (classDomain, actions: ActionsFor(classDomain, catalog)))
            .Where(x => x.actions.Count > 0)
            .Select(x => $"{x.classDomain}: {string.Join(", ", x.actions)}")
            .ToList();

    // Said once, above the table: on every class line it would put every read-only class into the
    // table to repeat one word. An action narrowed by an attribute gets its own line naming it,
    // and only when some entity admits it — a line for a file no directory has sends the model looking.
    private static IReadOnlyList<string> EveryEntityActions(HaCatalog catalog) =>
        catalog.Services
            .Where(svc => svc.AppliesToEveryEntity)
            .Where(svc => catalog.Entities.Any(e => HaActionResolver.ServicesFor(e, [svc]).Count > 0))
            .GroupBy(svc => svc.RequiresAttribute, StringComparer.Ordinal)
            .OrderBy(g => g.Key is null ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
                (g.Key is null ? "every entity: " : $"every entity with {g.Key}: ")
                + string.Join(", ", g.Select(svc => $"{svc.Service}.sh").Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal)))
            .ToList();

    private static IReadOnlyList<string> ActionsFor(string classDomain, HaCatalog catalog) =>
        catalog.ObjectIdsFor(classDomain)
            .Select(objectId => catalog.EntityById($"{classDomain}.{objectId}")!)
            .SelectMany(entity => HaActionResolver
                .ServicesFor(entity, catalog.Services)
                .Where(svc => !svc.AppliesToEveryEntity)
                .Select(svc => $"{HaActionResolver.CommandName(svc, classDomain)}.sh"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

}