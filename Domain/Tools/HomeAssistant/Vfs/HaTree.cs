using Domain.Contracts;
using Domain.Tools.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaTree
{
    // The watches subtree is listed from the ids handed in rather than from the catalog: watches are
    // read live from the home on every glob that can reach them, while the catalog is a cache.
    public static IReadOnlyList<string> Directories(HaCatalog catalog, IReadOnlyList<string>? watchIds = null)
    {
        var dirs = new List<string> { "entities", "areas", HaVfsPath.WatchesRootName };
        dirs.AddRange((watchIds ?? []).Select(id => $"{HaVfsPath.WatchesRootName}/{id}"));

        dirs.AddRange(catalog.ClassDomains().Select(c => $"entities/{c}"));
        dirs.AddRange(catalog.Entities.Select(e =>
            $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}"));

        foreach (var area in catalog.AreaSlugs())
        {
            dirs.Add($"areas/{area}");
            dirs.AddRange(catalog.EntityIdsInArea(area).Select(id =>
                $"areas/{area}/{HaSlug.Compose(id, HaCatalog.FriendlyName(catalog.EntityById(id)))}"));
        }

        return dirs.OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> Files(HaCatalog catalog, IReadOnlyList<string>? watchIds = null)
    {
        var files = new List<string>();
        files.AddRange((watchIds ?? []).SelectMany(id => new[]
        {
            $"{HaVfsPath.WatchesRootName}/{id}/{HaVfsPath.WatchFileName}",
            $"{HaVfsPath.WatchesRootName}/{id}/{HaVfsPath.WatchStatusFileName}"
        }));

        foreach (var e in catalog.Entities)
        {
            var entDir = $"entities/{HaCatalog.ClassOf(e.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(e.EntityId), HaCatalog.FriendlyName(e))}";
            files.AddRange(LeafFiles(entDir, e, catalog));
        }

        foreach (var area in catalog.AreaSlugs())
        {
            foreach (var id in catalog.EntityIdsInArea(area))
            {
                var entity = catalog.EntityById(id)!;
                var entDir = $"areas/{area}/{HaSlug.Compose(id, HaCatalog.FriendlyName(entity))}";
                files.AddRange(LeafFiles(entDir, entity, catalog));
            }
        }

        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> LeafFiles(string entityDir, HaEntityState entity, HaCatalog catalog)
    {
        yield return $"{entityDir}/{HaVfsPath.StateFileName}";
        var classDomain = HaCatalog.ClassOf(entity.EntityId);
        foreach (var svc in HaActionResolver.ServicesFor(entity, catalog.Services))
        {
            yield return $"{entityDir}/{HaActionResolver.CommandName(svc, classDomain)}.sh";
        }
    }

    public static IReadOnlyList<string> Glob(HaCatalog catalog, GlobScope scope, IReadOnlyList<string>? watchIds = null)
    {
        var dirs = Directories(catalog, watchIds).Where(scope.Matches).Select(p => p + "/");
        if (scope.DirsOnly)
        {
            return dirs.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        var files = Files(catalog, watchIds).Where(scope.Matches);
        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
}