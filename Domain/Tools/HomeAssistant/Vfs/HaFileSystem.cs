using System.Text.Json;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem(
    HaCatalogProvider catalogProvider,
    Func<IHomeAssistantClient> clientFactory,
    TimeSpan? regexMatchTimeout = null,
    Func<IMusicAssistantClient>? musicClientFactory = null,
    TimeProvider? timeProvider = null,
    Func<ConversationContext?>? caller = null) : FileSystemBackendBase
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly HaWatches _watches = new(clientFactory, timeProvider);

    // Who is writing, for the one record on this mount that remembers its author: a watch runs its
    // prompts as the agent that created it. The call-tool filter enters the context; a test hands
    // one in directly.
    private readonly Func<ConversationContext?> _caller = caller ?? (() => CallerContext.Current);

    public const string Name = "ha";

    public override string FilesystemName => Name;

    protected override TimeSpan SearchMatchTimeout => regexMatchTimeout ?? base.SearchMatchTimeout;

    public override string DescribeMount =>
        "Home Assistant as a filesystem. Browse `/ha/entities/<class>/<id>/` or "
        + "`/ha/areas/<room>/<entity_id>/`. `read state.json` for live state; `read <service>.sh` "
        + "(or `exec '<service>.sh --help'`) for an action's arguments; `exec '<service>.sh --flag "
        + "value'` to control a device. NOT a shell — exec only runs the listed *.sh action files "
        + "(anything else returns exit 127). The one writable place is `/ha/watches/<id>/watch.json`: "
        + "a standing instruction the home runs when an entity meets a condition (see the guide). "
        + "Everything else is read-only.";

    // The words the model reads about each operation, next to the behaviour they describe. They
    // name the mount's real files, which is what makes the Home Assistant surface usable.
    public override string DescribeRead =>
        "Reads a Home Assistant virtual file: state.json returns the entity's live state + "
        + "attributes; a *.sh file returns its usage (same as --help); /ha/watches/<id>/watch.json "
        + "is a watch as written, status.json beside it its createdAt/lastTriggeredAt/spent.";

    public override string DescribeCreate =>
        "Creates or replaces a watch: fs_create /ha/watches/<descriptive-id>/watch.json with JSON "
        + "{name, triggers, conditions?, effects, once?, enabled?, deliverTo?, userId?}. Writing an "
        + "existing id with overwrite=true replaces that watch in place. Nothing else on /ha can be "
        + "created.";

    public override string DescribeEdit =>
        "Edits a watch's /ha/watches/<id>/watch.json in place (threshold, effects, enabled, once); "
        + "the same watch is replaced, never a second one. Nothing else on /ha can be edited.";

    public override string DescribeDelete =>
        "Removes a watch: fs_delete /ha/watches/<id> (or its watch.json) deletes the automation from "
        + "the home. Nothing else on /ha can be deleted.";

    public override string DescribeInfo =>
        "Returns metadata for a Home Assistant virtual path: exists, isDirectory. Cheap existence "
        + "check before read/exec.";

    public override string DescribeGlob =>
        "Lists Home Assistant entities, areas, watches and action files matching a glob pattern. "
        + "`*` matches one path segment, `**` recurses. A trailing slash lists directories only "
        + "(domains, entities, areas, watches — e.g. `*/`); otherwise files (`state.json`, `*.sh`, "
        + "`watch.json`) and directories both match, with directories returned with a trailing slash.";

    public override string DescribeSearch =>
        "Searches Home Assistant entity state files (entity_id, friendly_name, attributes). Scope "
        + "with directoryPath (e.g. /ha/entities/light or /ha/areas/salon) or path (a single "
        + "state.json); omit both to search every entity. Use to find e.g. everything currently 'on'.";

    public override string DescribeExec =>
        "Runs a Home Assistant action file (a service call). path is the entity directory CWD "
        + "(e.g. /ha/entities/light/kitchen); command is an action file invocation like "
        + "'turn_on.sh --brightness_pct 60'. Use '<service>.sh --help' to see arguments. This is "
        + "NOT a shell — only *.sh action files run; anything else returns exit 127.";

    // Glob is uncapped: the result set is bounded by the home's entity count.
    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        var catalog = await catalogProvider.GetAsync(ct);
        var watchIds = await WatchIdsInScopeAsync(basePath, ct);
        return Glob(pattern, () => HaTree.Glob(catalog, scope, watchIds));
    }

    // The watches are read live from the home, so they are fetched only for a glob that can reach
    // them: one scoped to an entity or an area lists no watch and pays no call for them.
    private async Task<IReadOnlyList<string>> WatchIdsInScopeAsync(string basePath, CancellationToken ct)
    {
        var scope = (basePath ?? string.Empty).Trim('/');
        if (scope.Length > 0 && !scope.StartsWith(HaVfsPath.WatchesRootName, StringComparison.Ordinal))
        {
            return [];
        }

        return (await _watches.ListAsync(ct)).Select(w => w.Id).ToList();
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        if (IsWatchNode(node))
        {
            var (watchExists, watchIsDir) = await ResolveWatchAsync(node, ct);
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = watchExists, Path = path, IsDirectory = watchExists ? watchIsDir : null });
        }

        var catalog = await catalogProvider.GetAsync(ct);
        var (exists, isDir) = Resolve(node, catalog);

        return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = exists, Path = path, IsDirectory = exists ? isDir : null });
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = HaVfsPath.Parse(path);
        if (node.Kind is HaVfsKind.WatchFile or HaVfsKind.WatchStatusFile)
        {
            return await ReadWatchAsync(path, node, offset, limit, ct);
        }

        if (node.Kind is not (HaVfsKind.StateFile or HaVfsKind.ActionFile))
        {
            return NotFound(path);
        }

        var catalog = await catalogProvider.GetAsync(ct);
        var resolution = ResolveEntity(catalog, node);
        if (resolution.Entity is null)
        {
            return NotFound(path, resolution.Hint);
        }

        return node.Kind == HaVfsKind.StateFile
            ? await ReadStateAsync(path, resolution.Entity.EntityId, offset, limit, ct)
            : ReadAction(path, resolution.Entity, node.Service!, catalog);
    }

    public override async Task<FsResult<FsSearchResult>> SearchAsync(
        string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        // Search must reflect live state — values change within a single agent loop. Fetch fresh
        // states (one bulk GET /api/states) and overlay them on the cached structure (areas/services
        // rarely change). glob/info keep the cached catalog; read is already a live per-entity GET.
        var catalog = (await catalogProvider.GetAsync(ct)) with
        {
            Entities = await clientFactory().ListStatesAsync(ct)
        };

        if (!CompileFilePattern(filePattern).TryGetValue(out var admits, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        // state.json is the only searchable file per entity, so a filePattern either includes it
        // (search the scoped entities) or excludes it entirely (nothing to search).
        var scoped = admits(HaVfsPath.StateFileName)
            ? ScopeEntities(catalog, path, directoryPath)
            : [];

        return await SearchNodesAsync(
            scoped,
            (entity, _) => ValueTask.FromResult<(string, string?)>(
                (CanonicalStatePath(entity), HaStateRenderer.ToJson(entity))),
            new FsSearchScan
            {
                Query = query,
                Regex = regex,
                Path = path ?? directoryPath ?? string.Empty,
                MaxResults = maxResults,
                ContextLines = contextLines,
                OutputMode = outputMode
            },
            ct);
    }

    // Restricts the searched entity set to the requested scope: `path` (a single state file) or
    // `directoryPath` (a class/area/entity subtree). Null/root scope searches everything; an action
    // file or unknown path scopes to nothing (action files are read via read/--help, not searched).
    private static IReadOnlyList<HaEntityState> ScopeEntities(HaCatalog catalog, string? path, string? directoryPath)
    {
        var scope = path ?? directoryPath;
        if (string.IsNullOrWhiteSpace(scope))
        {
            return catalog.Entities;
        }
        var node = HaVfsPath.Parse(scope);
        return node.Kind switch
        {
            HaVfsKind.Root or HaVfsKind.EntitiesRoot or HaVfsKind.AreasRoot => catalog.Entities,
            HaVfsKind.ClassDir => catalog.Entities
                .Where(e => HaCatalog.ClassOf(e.EntityId).Equals(node.ClassDomain, StringComparison.Ordinal))
                .ToList(),
            HaVfsKind.AreaDir => catalog.EntityIdsInArea(node.Area!)
                .Select(catalog.EntityById)
                .OfType<HaEntityState>()
                .ToList(),
            HaVfsKind.EntityDir or HaVfsKind.StateFile =>
                ResolveEntity(catalog, node).Entity is { } entity ? [entity] : [],
            _ => []
        };
    }

    // Composes the entities-root form only — search hits are always reported under entities/, never
    // the area form, so callers get one canonical path per entity regardless of area membership.
    private static string CanonicalStatePath(HaEntityState entity) =>
        $"entities/{HaCatalog.ClassOf(entity.EntityId)}/{HaSlug.Compose(HaCatalog.ObjectOf(entity.EntityId), HaCatalog.FriendlyName(entity))}/{HaVfsPath.StateFileName}";

    private async Task<FsResult<FsReadResult>> ReadStateAsync(string path, string entityId, int? offset, int? limit, CancellationToken ct)
    {
        var entity = await clientFactory().GetStateAsync(entityId, ct);
        if (entity is null)
        {
            return NotFound(path);
        }

        return BuildReadResult(path, HaStateRenderer.ToJson(entity, await LivePositionAsync(entity, ct)), offset, limit);
    }

    // The position Home Assistant stores is stale between state transitions, so for a Music
    // Assistant player the queue is asked for the real one. Only a read of a single entity does
    // this: search renders every entity in the home, and a websocket round trip per player would
    // turn one bulk GET into a call per speaker for a field almost no search is about.
    //
    // MA is best-effort here. It is optional configuration, the socket can be down, and the queue
    // may know nothing about this player — in every one of those cases the read still returns Home
    // Assistant's own view rather than failing, because a state.json that errors is worse than one
    // carrying the value HA has.
    private async Task<MaQueuePosition?> LivePositionAsync(HaEntityState entity, CancellationToken ct)
    {
        if (musicClientFactory is null || QueueIdOf(entity) is not { } queueId)
        {
            return null;
        }

        try
        {
            var position = await musicClientFactory().GetQueuePositionAsync(queueId, ct);
            return IsTrustworthy(position) ? position : null;
        }
        catch (MusicAssistantException)
        {
            return null;
        }
    }

    // A queue MA is not playing has nothing live to report: its elapsed_time is whatever the last
    // transition left behind, which is the same stale number HA already carries. Taking it would
    // relabel a stale value as MA-sourced, so only a playing queue answers.
    //
    // Freshness beyond that is the caller's problem, not something this check can settle: MA
    // repeats the previous elapsed_time for a few seconds after a resume, and one sample cannot
    // tell that apart from a queue genuinely sitting at that position. The read is still strictly
    // better than HA's — stale by seconds after a resume rather than by the whole session — and the
    // stamp travels with it so a reader can see how old it is.
    private static bool IsTrustworthy(MaQueuePosition? position) =>
        position is { State: "playing" };

    private static string? QueueIdOf(HaEntityState entity) =>
        entity.Attributes.TryGetValue("active_queue", out var queue)
        && queue?.GetValueKind() is JsonValueKind.String
            ? queue.GetValue<string>()
            : null;

    private static FsResult<FsReadResult> ReadAction(string path, HaEntityState entity, string service, HaCatalog catalog)
    {
        var classDomain = HaCatalog.ClassOf(entity.EntityId);
        var svc = HaActionResolver.ServicesFor(entity, catalog.Services)
            .FirstOrDefault(s => HaActionResolver.CommandName(s, classDomain).Equals(service, StringComparison.Ordinal));
        return svc is null
            ? NotFound(path)
            : BuildReadResult(path, HaServiceHelpRenderer.Render(entity.EntityId, svc), null, null);
    }

    private readonly record struct EntityResolution(HaEntityState? Entity, string? Hint);

    // Strict canonical resolution: a segment resolves only if it equals the entity's composed
    // directory name (HaSlug.Compose). A recognizable object-id with a non-canonical segment yields
    // a hint naming the correct directory; an unknown object-id yields no hint. Keeps read/exec/info/
    // search in lockstep with glob, which composes the same names.
    private static EntityResolution ResolveEntity(HaCatalog catalog, HaVfsNode node)
    {
        var segment = node.EntitySegment!;
        var candidateId = node.Area is not null
            ? HaSlug.StripNice(segment)
            : $"{node.ClassDomain}.{HaSlug.StripNice(segment)}";

        var entity = catalog.EntityById(candidateId);
        if (entity is null)
        {
            return new EntityResolution(null, null);
        }

        var canonical = node.Area is not null
            ? HaSlug.Compose(entity.EntityId, HaCatalog.FriendlyName(entity))
            : HaSlug.Compose(HaCatalog.ObjectOf(entity.EntityId), HaCatalog.FriendlyName(entity));

        return segment == canonical
            ? new EntityResolution(entity, null)
            : new EntityResolution(null, canonical);
    }

    private static (bool Exists, bool IsDir) Resolve(HaVfsNode node, HaCatalog catalog) => node.Kind switch
    {
        HaVfsKind.Root or HaVfsKind.EntitiesRoot or HaVfsKind.AreasRoot => (true, true),
        HaVfsKind.ClassDir => (catalog.ClassDomains().Contains(node.ClassDomain), true),
        HaVfsKind.AreaDir => (catalog.AreaSlugs().Contains(node.Area), true),
        HaVfsKind.EntityDir => (ResolveEntity(catalog, node).Entity is not null, true),
        HaVfsKind.StateFile => (ResolveEntity(catalog, node).Entity is not null, false),
        HaVfsKind.ActionFile => ResolveEntity(catalog, node).Entity is { } e
            && HaActionResolver.ServicesFor(e, catalog.Services)
                .Any(s => HaActionResolver.CommandName(s, HaCatalog.ClassOf(e.EntityId)) == node.Service)
            ? (true, false)
            : (false, false),
        _ => (false, false)
    };

    // Line-numbered read result matching the Sandbox/Vault file_read shape.
    private static FsResult<FsReadResult> BuildReadResult(string filePath, string text, int? offset, int? limit)
    {
        var allLines = text.Split('\n');
        var start = Math.Clamp((offset ?? 1) - 1, 0, allLines.Length);
        var remaining = allLines.Skip(start).ToArray();
        var take = Math.Min(limit ?? remaining.Length, remaining.Length);
        var content = string.Join("\n", remaining.Take(take).Select((l, i) => $"{start + i + 1}: {l}"));
        var truncated = take < remaining.Length;

        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = filePath,
            Content = content,
            TotalLines = allLines.Length,
            Truncated = truncated,
            Suggestion = truncated ? $"Use offset={start + take + 1} to continue reading." : null
        });
    }

    // Home Assistant is a read + exec control surface with one writable subtree, the watches
    // (HaFileSystem.Watches.cs). Move, copy and raw byte streaming have no meaning anywhere on it,
    // so they are left unoverridden and the base answers them.

    private static FsResult<FsReadResult> NotFound(string path, string? canonicalName = null) =>
        new FsResult<FsReadResult>.Err(new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.NotFound,
            Message = $"No such path: {path}",
            Hint = canonicalName is null
                ? null
                : $"Use the exact directory name a listing returns: '{canonicalName}'."
        });
}