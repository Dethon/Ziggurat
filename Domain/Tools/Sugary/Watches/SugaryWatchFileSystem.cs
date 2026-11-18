using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Tools.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Domain.Tools.Sugary.Vfs;
using Domain.Tools.Watches;

namespace Domain.Tools.Sugary.Watches;

// The household's glucose watches, at /watchers beside the home's: a watch whose triggers are glucose
// kinds is Sugary's (a union member claiming them, FileSystemUnion), kept in Sugary's store and judged
// by Sugary on every poll. The file is `<id>/watch.json`, written and read back in canonical form,
// with a read-only `status.json` beside it.
//
// A glucose watch is about one person's sensor (its subject), so it is the business of that person,
// of whoever wrote it, and of whoever an owner let read everyone's Sugary log — nobody else in the
// household lists, reads, edits or removes it, and watching somebody else's sensor needs that grant
// when it is written. A call naming nobody is refused, as /sugary refuses it.
public sealed class SugaryWatchFileSystem(
    IGlucoseWatches watches, IHouseholdVoices voices, TimeZoneInfo serverZone, TimeProvider time) : FileSystemBackendBase
{
    public const string Name = "sugary-watches";

    // The conversation writing (its agent runs the watch's prompts, its origin is where a fire is
    // delivered by default), the account it acts for, and the household the call proved. Null on the
    // server's one instance; the registrar asks for a view per call.
    private ConversationContext? _caller;
    private Account? _reader;
    private ICallHousehold? _household;

    // The delegation the agent signed for this call's write, which the watch keeps as who its prompts run
    // as: none for a call that carried none, which leaves the watch nobody's.
    private string? _delegation;

    // A shallow copy with the caller swapped, as HaWatchFileSystem does: every dependency comes along.
    public override FileSystemBackendBase For(FileSystemCaller caller)
    {
        var view = (SugaryWatchFileSystem)MemberwiseClone();
        view._caller = caller.Conversation;
        view._reader = Principal.Of(caller.Conversation)?.Account;
        view._household = caller.Household;
        view._delegation = caller.Delegation;
        return view;
    }

    public override string FilesystemName => Name;

    public override string MountPoint => Watchers.MountPoint;

    public override FileSystemUnion? Union => new(Watchers.RouteBy, [.. GlucoseTrigger.Kinds]);

    public override string DescribeMount =>
        "Glucose watches: standing instructions Sugary runs when someone's glucose crosses a level, changes fast, "
        + "is forecast to cross one, or their sensor stops, fails to link or is ending — each "
        + "`/watchers/<id>/watch.json` (create, edit, delete) with a read-only `status.json` beside it, in mg/dL. "
        + "A watch whose triggers are glucose, glucose_trend, glucose_forecast or sensor is one of these, "
        + "shown to the person it watches, its writer and whoever may read everyone's log. Load the "
        + "`glucose-watches` skill first.";

    public override string DescribeRead =>
        "Reads a watch: /watchers/<id>/watch.json is the watch as written, status.json beside it when it was "
        + "created and last fired, whether it is spent, whose sensor it watches and whether each trigger holds now.";

    public override string DescribeCreate =>
        "Creates or replaces a watch: fs_create /watchers/<descriptive-id>/watch.json with JSON "
        + "{name, triggers, conditions?, effects, once?, enabled?, deliverTo?, userId?}. Writing an "
        + "existing id with overwrite=true replaces that watch in place.";

    public override string DescribeEdit =>
        "Edits a watch's /watchers/<id>/watch.json in place (threshold, effects, enabled, once); the same "
        + "watch is replaced, never a second one.";

    public override string DescribeDelete => "Removes a watch: fs_delete /watchers/<id> (or its watch.json).";

    public override string DescribeInfo => "Returns metadata for a watch path: exists and isDirectory.";

    public override string DescribeGlob =>
        "Lists the watches: each is a directory `<id>/` holding `watch.json` and `status.json`. "
        + "`*` matches one path segment, `**` recurses; a trailing slash lists directories only.";

    private const string ForNobody =
        "This turn acts for nobody signed in, so it has no glucose watches: a glucose watch is about a person's "
        + "sensor, and a guest or a turn with no person behind it has none.";

    private const string NotGranted =
        "This turn's person may watch only their own glucose: another person's sensor is watched only by someone "
        + "an owner of the household lets read everyone's Sugary log, on the household's members page.";

    private static FsResult<T> Nobody<T>() where T : class => Fail<T>(ToolError.Codes.PermissionDenied, ForNobody);

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsGlobResult>();
        }

        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        var people = await PeopleAsync(reader, ct);
        var ids = (await watches.ListAsync(reader, people.Granted, ct)).Select(w => w.Id).ToList();
        return Glob(pattern, () => Entries(ids, scope));
    }

    private static IReadOnlyList<string> Entries(IReadOnlyList<string> ids, GlobScope scope)
    {
        var dirs = ids.Where(scope.Matches).Select(id => id + "/");
        var files = scope.DirsOnly
            ? []
            : ids.SelectMany(id => new[] { $"{id}/{Watchers.WatchFileName}", $"{id}/{Watchers.StatusFileName}" })
                .Where(scope.Matches);
        return dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsInfoResult>();
        }

        var node = HaWatchPath.Parse(path);
        if (node.Kind is HaWatchKind.Root or HaWatchKind.Unknown)
        {
            var root = node.Kind == HaWatchKind.Root;
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = root, Path = path, IsDirectory = root ? true : null });
        }

        // An id somebody else keeps out of this reader's view is said to be taken and nothing more, so the
        // union refuses a home watch under it as this mount refuses a glucose one (UnionFileSystem).
        var stored = HaWatchAutomation.IsValidWatchId(node.WatchId) ? await watches.FindAsync(reader, node.WatchId!, ct) : null;
        if (stored is not null && !(await PeopleAsync(reader, ct)).Sees(stored))
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path, Hidden = true });
        }

        var exists = stored is not null;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists, Path = path, IsDirectory = exists ? node.Kind == HaWatchKind.WatchDir : null
        });
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        (await RenderAsync(path, ct)).TryGetValue(out var text, out var error)
            ? HaReadResult.Numbered(path, text, offset, limit)
            : new FsResult<FsReadResult>.Err(error);

    // The rendered file as it is, with no line numbers: what a sandbox command reads through the bridge.
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!(await RenderAsync(path, ct)).TryGetValue(out var text, out var error))
        {
            throw new FileSystemOperationException(error);
        }

        yield return Encoding.UTF8.GetBytes(text);
    }

    // A refusal thrown from the stream above would reach the wire as a failure to retry, so the byte
    // read answers it here, as /sugary does.
    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(string path, long offset, int length, CancellationToken ct) =>
        (await RenderAsync(path, ct)).TryGetValue(out _, out var error)
            ? await base.ReadBlobAsync(path, offset, length, ct)
            : new FsResult<FsBlobReadResult>.Err(error);

    private async Task<FsResult<string>> RenderAsync(string path, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<string>();
        }

        var node = HaWatchPath.Parse(path);
        if (node.Kind is not (HaWatchKind.WatchFile or HaWatchKind.WatchStatusFile))
        {
            return node.Kind is HaWatchKind.WatchDir or HaWatchKind.Root
                ? Invalid<string>($"'{path}' is a directory; glob it to see its files.")
                : NotFound<string>(path);
        }

        var people = await PeopleAsync(reader, ct);
        if (await VisibleAsync(reader, node.WatchId, people, ct) is not { } watch)
        {
            return NotFound<string>(path);
        }

        return new FsResult<string>.Ok(node.Kind == HaWatchKind.WatchFile
            ? WatchFor(watch, people).ToJson()
            : await StatusAsync(reader, watch, people, ct));
    }

    // The watch as this reader reads it: whose sensor it watches is said in the reader's own words — left
    // out for the person it is about, their folder under /sugary/people for anyone else — so writing back
    // what was read keeps the subject whoever reads it.
    private static GlucoseWatchSpec WatchFor(StoredGlucoseWatch watch, People people)
    {
        var spec = GlucoseWatchSpec.Parse(watch.Spec) with { Enabled = watch.Enabled };
        var person = people.PersonOf(watch.SubjectUserId);
        return spec with { Triggers = [.. spec.Triggers.Select(t => t with { Person = person })] };
    }

    private async Task<string> StatusAsync(Account reader, StoredGlucoseWatch watch, People people, CancellationToken ct)
    {
        var zone = await _household.ZoneAsync(serverZone, ct);
        var conditions = (await watches.ConditionsAsync(reader, watch.Id, ct)).ToDictionary(c => c.TriggerIndex);
        var triggers = GlucoseWatchSpec.Parse(watch.Spec).Triggers;
        return new JsonObject
        {
            ["createdAt"] = Stamp(watch.CreatedAt, zone),
            ["lastFiredAt"] = watch.LastFiredAt is { } fired ? Stamp(fired, zone) : null,
            ["enabled"] = watch.Enabled,
            ["spent"] = watch.Spent,
            ["subject"] = people.NameOf(watch.SubjectUserId),
            ["person"] = people.PersonOf(watch.SubjectUserId),
            // Null until the watch has been judged on a reading: nothing is known of it yet.
            ["triggers"] = new JsonArray([.. triggers.Select((trigger, index) => (JsonNode)new JsonObject
            {
                ["trigger"] = trigger.Kind,
                ["holds"] = conditions.TryGetValue(index, out var c) ? c.Holding : null,
                ["since"] = conditions.TryGetValue(index, out var h) && h is { Holding: true, HoldingSince: { } since }
                    ? Stamp(since, zone)
                    : null
            })])
        }.ToJsonString(_indented);
    }

    private static string Stamp(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    public override async Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsCreateResult>();
        }

        var node = HaWatchPath.Parse(path);
        if (node.Kind != HaWatchKind.WatchFile)
        {
            return RefuseWrite<FsCreateResult>(node, path, "created");
        }

        if (!HaWatchAutomation.IsValidWatchId(node.WatchId))
        {
            return InvalidWatchId<FsCreateResult>(node.WatchId);
        }

        var people = await PeopleAsync(reader, ct);
        var existing = await watches.FindAsync(reader, node.WatchId!, ct);
        if (existing is not null && !people.Sees(existing))
        {
            // An id is the household's: one somebody else keeps is taken, whatever overwrite says, and
            // nothing more about it is said.
            return Fail<FsCreateResult>(ToolError.Codes.AlreadyExists,
                $"The household already has a watch '{node.WatchId}' that is not this person's to see.",
                "Choose another id for this watch.");
        }

        if (existing is not null && !overwrite)
        {
            return Fail<FsCreateResult>(ToolError.Codes.AlreadyExists,
                $"Watch '{node.WatchId}' already exists.",
                "Pass overwrite=true to replace it in place (same id, never a second watch), or fs_edit its watch.json.");
        }

        return await WriteAsync(reader, people, node.WatchId!, content, existing, ct, () => new FsCreateResult
        {
            Status = existing is null ? "created" : "replaced",
            FilePath = path,
            Size = content.Length.ToString(CultureInfo.InvariantCulture),
            Lines = content.Split('\n').Length
        });
    }

    public override async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsEditResult>();
        }

        var node = HaWatchPath.Parse(path);
        if (node.Kind != HaWatchKind.WatchFile)
        {
            return RefuseWrite<FsEditResult>(node, path, "edited");
        }

        var people = await PeopleAsync(reader, ct);
        if (await VisibleAsync(reader, node.WatchId, people, ct) is not { } existing)
        {
            return NotFound<FsEditResult>(path);
        }

        if (!TextEdits.Apply(WatchFor(existing, people).ToJson(), edits).TryGetValue(out var applied, out var unmatched))
        {
            return new FsResult<FsEditResult>.Err(unmatched with
            {
                Hint = "Read the current watch.json and use its exact text; the file is rendered from the stored watch, so the spelling is the mount's."
            });
        }

        return await WriteAsync(reader, people, node.WatchId!, applied.Text, existing, ct, () => new FsEditResult
        {
            Status = "edited",
            FilePath = path,
            TotalOccurrencesReplaced = applied.Total,
            Edits = applied.Details
        });
    }

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsRemoveResult>();
        }

        var node = HaWatchPath.Parse(path);
        if (node.Kind is not (HaWatchKind.WatchDir or HaWatchKind.WatchFile))
        {
            return RefuseWrite<FsRemoveResult>(node, path, "deleted");
        }

        if (await VisibleAsync(reader, node.WatchId, ct) is null || !await watches.RemoveAsync(reader, node.WatchId!, ct))
        {
            return NotFound<FsRemoveResult>(path);
        }

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "deleted", Message = "the glucose watch was removed", OriginalPath = path, TrashPath = ""
        });
    }

    // Parse, find whose sensor it watches, fill what the caller leaves out, check the satellites it
    // names, and keep it.
    private async Task<FsResult<T>> WriteAsync<T>(
        Account writer, People people, string id, string content, StoredGlucoseWatch? existing, CancellationToken ct, Func<T> ok)
        where T : class
    {
        GlucoseWatchSpec spec;
        try
        {
            spec = GlucoseWatchSpec.Parse(content);
        }
        catch (GlucoseWatchSpecException ex)
        {
            return Fail<T>(ToolError.Codes.InvalidArgument, $"Invalid watch.json: {ex.Message}",
                "Fix the field named and write the file again; the glucose-watches skill lists the shape.");
        }

        if ((existing?.AgentId ?? _caller?.AgentId) is not { } agentId)
        {
            return Fail<T>(ToolError.Codes.InvalidArgument,
                "This call carries no conversation context, so the watch has no creating agent to run its prompts.",
                "Call from an agent turn; a watch is created by the agent that will answer its fires.");
        }

        if (!SubjectOf(spec, writer, people, existing).TryGetValue(out var subject, out var refused))
        {
            return new FsResult<T>.Err(refused);
        }

        // Where a fire is delivered and who it runs as, when the file names neither: the caller's own,
        // as a home watch takes them, and a replace naming none keeps the watch's own.
        var deliverTo = spec.DeliverTo
                        ?? existing?.DeliverTo
                        ?? (_caller?.Origin is { } origin
                            ? [origin.Address is null ? origin.ChannelId : $"{origin.ChannelId}:{origin.Address}"]
                            : null);
        var userId = spec.UserId ?? existing?.UserId ?? writer.UserId;
        if (userId != writer.UserId && userId != subject && userId != existing?.UserId)
        {
            return Fail<T>(ToolError.Codes.InvalidArgument,
                $"userId '{userId}' is neither the person writing this watch nor the person whose glucose it watches: "
                + "a glucose watch's fire runs for one of those two.",
                $"Leave userId out for this person ({writer.UserId})"
                + (subject == writer.UserId ? "." : $", or name the person it watches ({subject})."));
        }

        spec = spec with { DeliverTo = deliverTo, UserId = userId };

        if (await ValidateTargetsAsync(writer, spec, ct) is { } badTarget)
        {
            return new FsResult<T>.Err(badTarget);
        }

        var stored = new StoredGlucoseWatch
        {
            Id = id,
            SubjectUserId = subject,
            WriterUserId = existing?.WriterUserId ?? writer.UserId,
            AgentId = agentId,
            Spec = (spec with { Triggers = [.. spec.Triggers.Select(t => t with { Person = null })] }).ToJson(),
            Enabled = spec.Enabled,
            Once = spec.Once,
            DeliverTo = deliverTo,
            UserId = userId,
            // Every write replaces it: the last writer's turn is who the prompts run as, and a write the
            // agent signed nothing for leaves the watch nobody's.
            Delegation = GlucoseWatchDelegation.KeptFor(_delegation, writer, id),
            CreatedAt = existing?.CreatedAt ?? time.GetUtcNow(),
            LastFiredAt = existing?.LastFiredAt,
            // A one-shot that fired stays spent while it is written off, and is armed again by a write
            // that turns it on.
            Spent = existing is { Spent: true } && !spec.Enabled
        };

        var kept = existing is null
            ? await watches.AddAsync(writer, stored, ct)
            : await watches.ReplaceAsync(writer, stored, ct);
        return kept
            ? new FsResult<T>.Ok(ok())
            : Fail<T>(existing is null ? ToolError.Codes.AlreadyExists : ToolError.Codes.NotFound,
                existing is null
                    ? $"Watch '{id}' was created by another write a moment ago."
                    : $"Watch '{id}' was removed by another write a moment ago.",
                "Glob /watchers and read it again before writing.");
    }

    // Whose sensor the watch watches: every trigger must name the same person. A trigger naming nobody
    // is the watch's own subject on a replace, the writer's own sensor on a new one.
    private static FsResult<string> SubjectOf(GlucoseWatchSpec spec, Account writer, People people, StoredGlucoseWatch? existing)
    {
        var subjects = new List<(int Index, string UserId)>();
        foreach (var (trigger, index) in spec.Triggers.Select((t, i) => (t, i)))
        {
            if (trigger.Person is not { } person)
            {
                subjects.Add((index, existing?.SubjectUserId ?? writer.UserId));
                continue;
            }

            if (people.OwnFolder == person)
            {
                subjects.Add((index, writer.UserId));
                continue;
            }

            if (!people.Granted)
            {
                return Fail<string>(ToolError.Codes.PermissionDenied, $"triggers[{index}].person: {NotGranted}");
            }

            if (people.Named(person) is not { } named)
            {
                return Fail<string>(ToolError.Codes.InvalidArgument,
                    $"triggers[{index}].person '{person}' names nobody. The people whose glucose can be watched are: "
                    + string.Join(", ", people.Folders.DefaultIfEmpty("none")) + ".",
                    "Leave person out for this person's own sensor.");
            }

            subjects.Add((index, named));
        }

        if (subjects.Select(s => s.UserId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return Fail<string>(ToolError.Codes.InvalidArgument,
                "The triggers name more than one person's sensor ("
                + string.Join(", ", subjects.Select(s => $"triggers[{s.Index}]: {people.PersonOf(s.UserId) ?? "this person"}"))
                + "): a glucose watch watches one person.",
                "Write one watch for each person.");
        }

        var subject = subjects[0].UserId;
        return subject != writer.UserId && !people.Granted
            ? Fail<string>(ToolError.Codes.PermissionDenied, NotGranted)
            : new FsResult<string>.Ok(subject);
    }

    // An announcement's target is resolved against the household's voice server when the file is
    // written, the timers' rule: a target it cannot resolve is an alarm that rings nowhere. No target
    // rings on the people's own channels and needs no voice server.
    private async Task<ToolErrorResult?> ValidateTargetsAsync(Account writer, GlucoseWatchSpec spec, CancellationToken ct)
    {
        var targeted = spec.Effects
            .Select((effect, index) => (Effect: effect as GlucoseAnnounceEffect, Index: index))
            .Where(e => e.Effect?.Target is not null)
            .ToList();
        if (targeted.Count == 0)
        {
            return null;
        }

        try
        {
            var satellites = voices.For(writer.TenantId).Satellites;
            var roster = await satellites.GetAllAsync(ct);
            var known = roster.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var rooms = roster.Count == 0
                ? "none"
                : string.Join(", ", roster.Select(s => $"{s.Id} (room \"{s.Room}\")"));
            foreach (var (effect, index) in targeted)
            {
                var target = effect!.Target!.Deserialize<AnnounceTarget>(_targetJson);
                if (target is null
                    || (target.SatelliteId is null && target.SatelliteIds is not { Count: > 0 } && target.Room is null && target.All != true))
                {
                    return ToolError.Result(ToolError.Codes.InvalidArgument,
                        $"effects[{index}].target must name satellites: {{satelliteId | satelliteIds | room | all}}, "
                        + "or be left out to ring on the person's own channels.",
                        $"The satellites are {rooms}.");
                }

                IEnumerable<string> named = target.SatelliteIds is { Count: > 0 } ids
                    ? ids.Where(i => i is not null)
                    : target.SatelliteId is not null ? [target.SatelliteId] : [];
                var unknown = named.Where(i => !known.Contains(i)).ToList();
                if (unknown.Count > 0)
                {
                    return ToolError.Result(ToolError.Codes.NotFound,
                        $"effects[{index}].target names unknown satellite(s): {string.Join(", ", unknown)}. Known satellites: {rooms}.",
                        "Use one of those ids or rooms, or leave target out to ring on the person's own channels.");
                }

                if ((await satellites.ResolveAsync(target, ct)).Count == 0)
                {
                    return ToolError.Result(ToolError.Codes.NotFound,
                        $"effects[{index}].target {effect.Target!.ToJsonString()} matches no satellite. Known satellites: {rooms}.",
                        "Use one of those ids or rooms, or leave target out to ring on the person's own channels.");
                }
            }
        }
        catch (VoiceHubUnavailableException ex)
        {
            return ToolError.Result(ToolError.Codes.TransientDependency,
                $"The household's voice server is not answering, so the announcement's target cannot be checked: {ex.Message} The watch was not written.",
                "Try again in a moment, or leave target out to ring the alarm on the person's own channels instead.");
        }
        catch (VoiceHubRejectedException ex)
        {
            var refusedToken = ex.StatusCode is 401 or 403;
            return ToolError.Result(refusedToken ? ToolError.Codes.Authentication : ToolError.Codes.TransientDependency,
                $"The household's voice server answered {ex.StatusCode} while checking the announcement's target: {ex.Message} The watch was not written.",
                refusedToken
                    ? "Sugary's credential for the voice relay was refused; that is the deployment's to fix, not the file's. Leave target out to ring on the person's own channels."
                    : "Try again in a moment, or leave target out to ring the alarm on the person's own channels instead.");
        }

        return null;
    }

    private static readonly JsonSerializerOptions _targetJson = new() { PropertyNameCaseInsensitive = true };

    private async Task<StoredGlucoseWatch?> VisibleAsync(Account reader, string? id, CancellationToken ct) =>
        await VisibleAsync(reader, id, await PeopleAsync(reader, ct), ct);

    private async Task<StoredGlucoseWatch?> VisibleAsync(Account reader, string? id, People people, CancellationToken ct) =>
        HaWatchAutomation.IsValidWatchId(id) && await watches.FindAsync(reader, id!, ct) is { } watch && people.Sees(watch)
            ? watch
            : null;

    // The reader's household's people as the call proved it, and whether the reader may see everyone's
    // glucose. A call that proved no household, or another than the reader's, holds no grant and names
    // nobody else.
    private async Task<People> PeopleAsync(Account reader, CancellationToken ct)
    {
        if (_household is not { } household || household.Id != reader.TenantId)
        {
            return new People(reader, []);
        }

        return new People(reader, (await household.SugaryPeopleAsync(ct)).People);
    }

    private static FsResult<T> InvalidWatchId<T>(string? id) where T : class =>
        FsError.Invalid<T>(
            $"'{id}' is not a valid watch id: use a descriptive slug of letters, digits, '-' and '_' (e.g. laura-low-at-night).");

    private static FsResult<T> RefuseWrite<T>(HaWatchNode node, string path, string verb) where T : class =>
        node.Kind is HaWatchKind.WatchStatusFile
            ? Fail<T>(ToolError.Codes.UnsupportedOperation,
                $"{path} is read-only: status.json is rendered from the watch; the watch is changed through /watchers/<id>/watch.json.",
                "Write or edit the watch.json beside it instead.")
            : Fail<T>(ToolError.Codes.UnsupportedOperation,
                $"{path} cannot be {verb}: the one writable file of a watch is /watchers/<id>/watch.json.",
                "Create the watch's watch.json; to remove a watch, delete its directory.");

    // The household's people as one reader names them: everyone else under the folder /sugary/people
    // shows them by (SugaryPath.FoldersOf), the reader under none.
    private sealed class People(Account reader, IReadOnlyList<SugaryPerson> everyone)
    {
        private readonly IReadOnlyList<(string Folder, SugaryPerson Person)> _others =
            SugaryPath.FoldersOf(everyone, reader.UserId);

        public bool Granted { get; } = everyone.Any(p => p.UserId == reader.UserId && p.ReadsEveryLog);

        // The reader's own name as a folder, which a trigger may name for the reader's own sensor.
        public string? OwnFolder { get; } = everyone.FirstOrDefault(p => p.UserId == reader.UserId) is { } self
            ? SugaryPath.FolderOf(self.DisplayName)
            : null;

        public IEnumerable<string> Folders => _others.Select(o => o.Folder);

        public bool Sees(StoredGlucoseWatch watch) =>
            watch.SubjectUserId == reader.UserId || watch.WriterUserId == reader.UserId || Granted;

        public string? Named(string folder) => _others.FirstOrDefault(o => o.Folder == folder).Person?.UserId;

        // How a trigger names this person to the reader: nothing for the reader, a folder for anyone
        // else, and their id for somebody the household no longer lists.
        public string? PersonOf(string userId) =>
            userId == reader.UserId ? null : _others.FirstOrDefault(o => o.Person.UserId == userId).Folder ?? userId;

        public string? NameOf(string userId) => everyone.FirstOrDefault(p => p.UserId == userId)?.DisplayName;
    }
}