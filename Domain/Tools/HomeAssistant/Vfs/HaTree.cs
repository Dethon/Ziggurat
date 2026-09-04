using Domain.Contracts;
using Domain.Tools.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaTree
{
    public static IReadOnlyList<string> Directories(HaCatalog catalog)
    {
        var dirs = new List<string> { "entities", "areas" };

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

    public static IReadOnlyList<string> Files(HaCatalog catalog)
    {
        var files = new List<string>();

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

    public static IReadOnlyList<string> Glob(HaCatalog catalog, GlobScope scope)
    {
        var dirs = Directories(catalog).Where(scope.Matches).Select(p => p + "/");
        if (scope.DirsOnly)
        {
            return dirs.OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        var files = Files(catalog).Where(scope.Matches);
        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
}