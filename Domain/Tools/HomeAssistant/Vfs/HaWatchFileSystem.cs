using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Tools.FileSystem;

namespace Domain.Tools.HomeAssistant.Vfs;

// The home's watches, at /watchers. A watch is a Home Assistant automation; the file is a view of
// it, rendered on write and projected back on read, stored nowhere but in the home. Every path but
// a watch file refuses a write here, by name, so the model learns where writing is possible
// instead of hunting for another spelling.
//
// /watchers is a mount point other servers' watches share (a union, FileSystemUnion): each watch
// is a top-level entry, and a new one comes here when none of its triggers is a kind another
// member claims — which is every Home Assistant trigger.
public sealed class HaWatchFileSystem(
    HaCatalogProvider catalogProvider,
    Func<IHomeAssistantClient> clientFactory,
    TimeProvider? timeProvider = null,
    ConversationContext? caller = null,
    HaWatches? watches = null,
    ISatelliteCatalog? satellites = null) : FileSystemBackendBase
{
    public const string Name = "ha-watches";
    public const string MountPointPath = "/watchers";

    // Shared with the home mount's setup index when the server hands one in, so both read the home's
    // watches through one instance; a test that builds the mount alone gets its own.
    private readonly HaWatches _watches = watches ?? new HaWatches(clientFactory, timeProvider);

    // Who is writing: a watch runs its prompts as the agent that created it, and acts for the person
    // whose write carried the delegation it keeps — or for nobody, when the write that last set it
    // carried none. The server's one instance has no caller, and the registrar asks for a view per
    // call.
    private ConversationContext? _caller = caller;
    private string? _delegation;

    // A shallow copy with the caller swapped, never a second construction: a copy carries every
    // dependency this mount was built with — the ones that exist and the ones added later — so
    // there is no argument list here to fall behind the constructor's. The watches and the catalog
    // are shared by reference, which is the point; the class is sealed, so the copy is exactly this
    // type. HaWatchFileSystemCallerViewTests pins caller-and-nothing-else.
    public override FileSystemBackendBase For(FileSystemCaller caller)
    {
        var view = (HaWatchFileSystem)MemberwiseClone();
        view._caller = caller.Conversation;
        view._delegation = caller.Delegation;
        return view;
    }

    public override string FilesystemName => Name;

    public override string MountPoint => MountPointPath;

    // Every trigger Home Assistant knows is this member's: it claims no value and takes whatever no
    // other member claims.
    public override FileSystemUnion? Union => new("/triggers/*/trigger", [], Fallback: true);

    public override string DescribeMount =>
        "The home's watches: standing instructions the home runs when an entity meets a condition, "
        + "each a Home Assistant automation written as `/watchers/<id>/watch.json` (create, edit, "
        + "delete), with a read-only `status.json` beside it. Load the `home-watches` skill first.";

    public override string DescribeRead =>
        "Reads a watch: /watchers/<id>/watch.json is the watch as written, status.json beside it its "
        + "createdAt/lastTriggeredAt/spent.";

    public override string DescribeCreate =>
        "Creates or replaces a watch: fs_create /watchers/<descriptive-id>/watch.json with JSON "
        + "{name, triggers, conditions?, effects, once?, enabled?, deliverTo?, userId?}. Writing an "
        + "existing id with overwrite=true replaces that watch in place.";

    public override string DescribeEdit =>
        "Edits a watch's /watchers/<id>/watch.json in place (threshold, effects, enabled, once); "
        + "the same watch is replaced, never a second one.";

    public override string DescribeDelete =>
        "Removes a watch: fs_delete /watchers/<id> (or its watch.json) deletes the automation from "
        + "the home.";

    public override string DescribeInfo =>
        "Returns metadata for a watch path: exists and isDirectory.";

    public override string DescribeGlob =>
        "Lists the watches: each is a directory `<id>/` holding `watch.json` and `status.json`. "
        + "`*` matches one path segment, `**` recurses; a trailing slash lists directories only.";

    // Read live from the home on every listing. A home that cannot answer is a failure, not an
    // empty listing: beside other members of /watchers, an empty answer would read as "no home
    // watches" where the truth is "the home did not answer".
    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        IReadOnlyList<string> ids;
        try
        {
            ids = (await _watches.ListAsync(ct)).Select(w => w.Id).ToList();
        }
        catch (HomeAssistantException ex)
        {
            return HomeAssistantFailure<FsGlobResult>(ex);
        }

        return Glob(pattern, () => Entries(ids, scope));
    }

    private static IReadOnlyList<string> Entries(IReadOnlyList<string> ids, GlobScope scope)
    {
        var dirs = ids.Where(scope.Matches).Select(id => id + "/");
        var files = scope.DirsOnly
            ? []
            : ids.SelectMany(id => new[] { $"{id}/{HaWatchPath.WatchFileName}", $"{id}/{HaWatchPath.StatusFileName}" })
                .Where(scope.Matches);
        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = HaWatchPath.Parse(path);
        var (exists, isDir) = node.Kind switch
        {
            HaWatchKind.Root => (true, true),
            HaWatchKind.Unknown => (false, false),
            _ => (HaWatchAutomation.IsValidWatchId(node.WatchId) && await _watches.GetAsync(node.WatchId!, ct) is not null,
                node.Kind == HaWatchKind.WatchDir)
        };
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = exists, Path = path, IsDirectory = exists ? isDir : null });
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        (await RenderAsync(path, ct)).TryGetValue(out var text, out var error)
            ? HaReadResult.Numbered(path, text, offset, limit)
            : new FsResult<FsReadResult>.Err(error);

    // The rendered file as it is, with no line numbers: what a sandbox command reads through the
    // bridge, so `jq .` parses a watch and `sed -i` on one writes back what it read.
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!(await RenderAsync(path, ct)).TryGetValue(out var text, out var error))
        {
            throw new FileSystemOperationException(error);
        }

        yield return Encoding.UTF8.GetBytes(text);
    }

    private async Task<FsResult<string>> RenderAsync(string path, CancellationToken ct)
    {
        var node = HaWatchPath.Parse(path);
        if (node.Kind is not (HaWatchKind.WatchFile or HaWatchKind.WatchStatusFile)
            || !HaWatchAutomation.IsValidWatchId(node.WatchId)
            || await _watches.GetAsync(node.WatchId!, ct) is not { } watch)
        {
            return NotFound<string>(path);
        }

        return new FsResult<string>.Ok(node.Kind == HaWatchKind.WatchFile
            ? watch.Spec.ToJson()
            : HaWatches.RenderStatus(watch, (await catalogProvider.GetAsync(ct)).HomeZone));
    }

    public override async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = HaWatchPath.Parse(path);
        if (node.Kind != HaWatchKind.WatchFile)
        {
            return RefuseWrite<FsCreateResult>(node, path, "created");
        }
        if (!HaWatchAutomation.IsValidWatchId(node.WatchId))
        {
            return InvalidWatchId<FsCreateResult>(node.WatchId);
        }

        var existing = await _watches.GetAsync(node.WatchId!, ct);
        if (existing is not null && !overwrite)
        {
            return FsError.Fail<FsCreateResult>(
                ToolError.Codes.AlreadyExists,
                $"Watch '{node.WatchId}' already exists.",
                "Pass overwrite=true to replace it in place (same id, never a second watch), or fs_edit its watch.json.");
        }

        return await WriteWatchAsync(node.WatchId!, content, existing, ct, note => new FsCreateResult
        {
            Status = existing is null ? "created" : "replaced",
            FilePath = path,
            Size = content.Length.ToString(),
            Lines = content.Split('\n').Length,
            Note = note
        });
    }

    public override async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = HaWatchPath.Parse(path);
        if (node.Kind != HaWatchKind.WatchFile)
        {
            return RefuseWrite<FsEditResult>(node, path, "edited");
        }
        if (!HaWatchAutomation.IsValidWatchId(node.WatchId) || await _watches.GetAsync(node.WatchId!, ct) is not { } existing)
        {
            return NotFound<FsEditResult>(path);
        }

        if (!TextEdits.Apply(existing.Spec.ToJson(), edits).TryGetValue(out var applied, out var unmatched))
        {
            return new FsResult<FsEditResult>.Err(unmatched with
            {
                Hint = "Read the current watch.json and use its exact text; the file is rendered from the automation, so the spelling is the mount's."
            });
        }

        return await WriteWatchAsync(node.WatchId!, applied.Text, existing, ct, note => new FsEditResult
        {
            Status = "edited",
            FilePath = path,
            TotalOccurrencesReplaced = applied.Total,
            Edits = applied.Details,
            Note = note
        });
    }

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = HaWatchPath.Parse(path);
        if (node.Kind is not (HaWatchKind.WatchDir or HaWatchKind.WatchFile))
        {
            return RefuseWrite<FsRemoveResult>(node, path, "deleted");
        }
        if (!HaWatchAutomation.IsValidWatchId(node.WatchId) || await _watches.GetAsync(node.WatchId!, ct) is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        try
        {
            await _watches.DeleteAsync(node.WatchId!, ct);
        }
        catch (HomeAssistantException ex)
        {
            return HomeAssistantFailure<FsRemoveResult>(ex);
        }

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "deleted", Message = "the automation was removed from the home", OriginalPath = path, TrashPath = ""
        });
    }

    // Parse, validate, render and hand to the home. The home's own refusal — a trigger it does not
    // know, a key it requires — comes back in its own words, so the agent can fix the file in the
    // same turn instead of creating a watch that never fires.
    private async Task<FsResult<T>> WriteWatchAsync<T>(string watchId, string content, HaWatch? existing, CancellationToken ct, Func<string?, T> ok)
        where T : class
    {
        HaWatchSpec spec;
        try
        {
            spec = HaWatchSpec.Parse(content);
        }
        catch (HaWatchSpecException ex)
        {
            return FsError.Fail<T>(ToolError.Codes.InvalidArgument, $"Invalid watch.json: {ex.Message}",
                "Fix the field named and write the file again; the guide lists the shape.");
        }

        var caller = _caller;
        var agentId = existing?.Meta.AgentId ?? caller?.AgentId;
        if (agentId is null)
        {
            return FsError.Fail<T>(ToolError.Codes.InvalidArgument,
                "This call carries no conversation context, so the watch has no creating agent to run its prompts.",
                "Call from an agent turn; a watch is created by the agent that will answer its fires.");
        }

        // "Warn me" is answered where it was asked. The model is never told which channel a turn
        // came from — only voice decorates the turn with its room — so a file that names no
        // delivery takes the caller's own origin: the speaking satellite on voice, Telegram on
        // Telegram, the chat on the chat. Naming one is how the user sends the answer elsewhere.
        // A replace that names none keeps the watch's own: "below 65, not 70" said on Telegram
        // must not move a warning that was asked for on voice.
        if (spec.DeliverTo is null)
        {
            spec = spec with
            {
                DeliverTo = existing?.Meta.DeliverTo
                    ?? (caller?.Origin is { } origin
                        ? [origin.Address is null ? origin.ChannelId : $"{origin.ChannelId}:{origin.Address}"]
                        : null)
            };
        }

        // The same rule for who the fire is for: the model is not told who is asking either, and a
        // fire attributed to nobody runs as the sender label the callback stamps on it — memory read
        // and written under a phantom user. A replace naming none keeps the watch's own, as above.
        if (spec.UserId is null)
        {
            spec = spec with { UserId = existing?.Meta.UserId ?? caller?.UserId };
        }

        try
        {
            if (await ValidateAnnounceTargetsAsync(spec, ct) is { } badTarget)
            {
                return new FsResult<T>.Err(badTarget);
            }
        }
        catch (VoiceHubUnavailableException ex)
        {
            return FsError.Fail<T>(ToolError.Codes.TransientDependency,
                $"The voice hub is not answering, so the announcement's target cannot be checked: {ex.Message}. The watch was not written.",
                "Try again in a moment; a target the hub cannot vouch for would fire into silence.");
        }
        catch (VoiceHubRejectedException ex)
        {
            // The hub answered, with an error: a refused token is configuration and retrying will
            // not fix it; anything else it answered is its own trouble and might.
            var refusedToken = ex.StatusCode is 401 or 403;
            return FsError.Fail<T>(refusedToken ? ToolError.Codes.Authentication : ToolError.Codes.TransientDependency,
                $"The voice hub answered {ex.StatusCode} while checking the announcement's target: {ex.Message} The watch was not written.",
                refusedToken
                    ? "The Home Assistant server's announce token does not match the voice hub's; that is the deployment's to fix, not the file's."
                    : "Try again in a moment; a target the hub cannot vouch for would fire into silence.");
        }

        HaWatch written;
        try
        {
            written = await _watches.WriteAsync(watchId, spec, agentId, existing, _delegation, ct);
        }
        catch (HomeAssistantConfigRejectedException ex)
        {
            return FsError.Fail<T>(ToolError.Codes.InvalidArgument, $"Home Assistant rejected the watch: {ex.Message}",
                "The triggers, conditions and actions are Home Assistant's own JSON; fix the key it names and write the file again.");
        }
        catch (HomeAssistantException ex)
        {
            return HomeAssistantFailure<T>(ex);
        }

        return new FsResult<T>.Ok(ok(PauseNote(spec, written)));
    }

    // The on/off is a service call on the entity the reload produces, and a brand-new entity can
    // trail the write past the store's patience. The config was written and the home holds it
    // armed, so a watch asked for paused says so rather than reporting a pause that did not happen.
    private static string? PauseNote(HaWatchSpec spec, HaWatch written) =>
        !spec.Enabled && written.State is null
            ? "The watch was written, but the home had not loaded its automation entity in time to pause it, so it is armed. "
              + "Write it again with \"enabled\": false in a moment to pause it."
            : null;

    // An announcement's target is resolved against the voice hub when the file is written, the
    // timers' rule: the hub knows rooms by its own names, not by Home Assistant's area slugs, and a
    // target it cannot resolve is a watch whose trace shows a fire while nothing rings. Without a
    // hub to ask, the target is written as it came.
    private async Task<ToolErrorResult?> ValidateAnnounceTargetsAsync(HaWatchSpec spec, CancellationToken ct)
    {
        var announces = spec.Effects.OfType<HaAnnounceEffect>().ToList();
        if (satellites is null || announces.Count == 0)
        {
            return null;
        }

        var roster = await satellites.GetAllAsync(ct);
        var known = roster.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var rooms = string.Join(", ", roster.Select(s => $"{s.Id} (room \"{s.Room}\")"));
        foreach (var announce in announces)
        {
            var target = announce.Target.Deserialize<AnnounceTarget>(_targetJson);
            if (target is null
                || (target.SatelliteId is null && target.SatelliteIds is not { Count: > 0 } && target.Room is null && target.All != true))
            {
                return ToolError.Result(ToolError.Codes.InvalidArgument,
                    "An announce effect's target is required: {satelliteId | satelliteIds | room | all}.",
                    $"The satellites are {rooms}.");
            }

            IEnumerable<string> named = target.SatelliteIds is { Count: > 0 } ids
                ? ids.Where(id => id is not null)
                : target.SatelliteId is not null ? [target.SatelliteId] : [];
            var unknown = named.Where(id => !known.Contains(id)).ToList();
            if (unknown.Count > 0)
            {
                return ToolError.Result(ToolError.Codes.NotFound,
                    $"The announce target names unknown satellite(s): {string.Join(", ", unknown)}. Known satellites: {rooms}.",
                    "Use one of those ids, or the room name the hub lists.");
            }

            if ((await satellites.ResolveAsync(target, ct)).Count == 0)
            {
                return ToolError.Result(ToolError.Codes.NotFound,
                    $"The announce target {announce.Target.ToJsonString()} matches no satellite. Known satellites: {rooms}.",
                    "A room is the voice hub's room name as the timers section lists it (or a satellite id), never a Home Assistant area slug.");
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions _targetJson = new() { PropertyNameCaseInsensitive = true };

    private static FsResult<T> HomeAssistantFailure<T>(HomeAssistantException ex) where T : class =>
        ex is HomeAssistantNotFoundException
            ? FsError.Fail<T>(ToolError.Codes.NotFound, ex.Message,
                "If every watch write answers 404, this home lacks the `config` integration (part of default_config), so watches are unavailable here.")
            : FsError.Fail<T>(ToolError.CodeFor(ex), ex.Message);

    private static FsResult<T> InvalidWatchId<T>(string? id) where T : class =>
        FsError.Invalid<T>(
            $"'{id}' is not a valid watch id: use a descriptive slug of letters, digits, '-' and '_' (e.g. laura-sugar-high).");

    // A write anywhere but a watch file is refused by name, and the refusal says where writing is
    // possible instead.
    private static FsResult<T> RefuseWrite<T>(HaWatchNode node, string path, string verb) where T : class =>
        node.Kind is HaWatchKind.WatchStatusFile
            ? FsError.Fail<T>(ToolError.Codes.UnsupportedOperation,
                $"{path} is read-only: status.json is rendered from the automation; the watch is changed through /watchers/<id>/watch.json.",
                "Write or edit the watch.json beside it instead.")
            : FsError.Fail<T>(ToolError.Codes.UnsupportedOperation,
                $"{path} cannot be {verb}: the one writable file of a home watch is /watchers/<id>/watch.json.",
                "Create the watch's watch.json; to remove a watch, delete its directory.");
}
