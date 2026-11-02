using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Tools;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Timers.Vfs;

// A household's countdown timers as a VFS: create /<id>/timer.json to arm, read status.json for
// time left, delete the directory to cancel. Timers are immutable (delete and recreate) and fire
// once. /dismiss (exec) silences every alert the household has ringing — alarms and timers alike,
// on its satellites and on its people's channels — so "stop the alarm" works from any room or
// channel, not just by waking a targeted satellite.
public sealed class TimerFileSystem(
    ITimerStore store, TimeProvider timeProvider, IHouseholdVoices voices,
    IChannelAlerts alerts, TimeSpan? regexMatchTimeout = null) : FileSystemBackendBase
{
    public const string Name = "timers";

    // Whose timers this view sees and sets: the household the call's token named — a person's or
    // its guest's — and, for a person, their account and the agent their turn runs on, which a
    // timer with no satellite target rings for. Null on the server's own instance and for a call
    // acting for nobody, which is refused rather than shown somebody's timers.
    private string? _household;
    private Account? _owner;
    private string? _agentId;

    // The household as the call proved it, whose zone a timer's firing time is shown in.
    private ICallHousehold? _householdOfCall;

    // A shallow copy with the caller swapped, as ScheduleFileSystem does: the copy carries every
    // dependency this mount was built with, and the store is shared by reference.
    public override FileSystemBackendBase For(FileSystemCaller caller)
    {
        var view = (TimerFileSystem)MemberwiseClone();
        view._household = caller.Conversation?.Identity is { IsNobody: false, TenantId.Length: > 0 } identity
            ? identity.TenantId
            : null;
        view._owner = Principal.Of(caller.Conversation)?.Account;
        view._agentId = caller.Conversation?.AgentId;
        view._householdOfCall = caller.Household;
        return view;
    }

    public override string FilesystemName => Name;

    protected override TimeSpan SearchMatchTimeout => regexMatchTimeout ?? base.SearchMatchTimeout;

    // What the mount is, not how to drive it: the file's shape, the target rules and the dismiss
    // action are the countdown-timers skill's body, and a description that repeated them was a
    // reason to skip the load — a model with the JSON in front of it wrote the file and never
    // read the rules that go with it.
    //
    // It describes and does not command: a first cut said "before you create, read, change, cancel
    // or silence one, load the skill", and a model reading "cancel" and "silence" loaded the timers
    // skill for a calendar alarm's snooze and for a media restart. The skill's own description is
    // the trigger; this line only says where the shape lives.
    public override string DescribeMount =>
        "Short countdown timers that ring on the household's voice satellites, or on the person's chat "
        + "client and Telegram: each is a directory /timers/<id> holding its timer.json and a read-only "
        + "status.json (remainingSeconds, firesAt); /timers/satellites.json lists the satellites a timer "
        + "can ring on, and the action ./dismiss at /timers silences whatever is ringing (alarms and "
        + "timers) from any room or channel. The file's shape and the target rules are in the "
        + "`countdown-timers` skill, not here. Use the HA alarms calendar for clock-time "
        + "alarms/reminders, not timers.";

    // The words the model reads about each operation, next to the behaviour they describe. They
    // name the mount's real files, which is what makes the timers surface usable without a probe.
    public override string DescribeGlob =>
        "Lists timer filesystem entries matching a glob under basePath. `*` matches one "
        + "path segment, `**` recurses, `?` one char, `{a,b}` brace alternation. A trailing slash "
        + "(e.g. `*/`) lists directories only; otherwise files and directories both match, with "
        + "directory results marked by a trailing slash.";

    public override string DescribeInfo => "Get info about a timer filesystem path";

    public override string DescribeRead => "Read a timer filesystem file (timer.json/status.json/satellites.json)";

    public override string DescribeSearch =>
        "Searches timer.json content across timers. Scope with path (a single timer); omit to "
        + "search every timer. Supports regex, filePattern, maxResults, contextLines, and "
        + "outputMode (content|filesOnly) like the other filesystems.";

    public override string DescribeCreate =>
        "Arm a timer: fs_create /<descriptive-id>/timer.json with JSON {durationSeconds, text?, target?}";

    public override string DescribeDelete => "Cancel a timer by deleting its directory /<timerId>";

    public override string DescribeExec =>
        $"Silence every alert currently ringing: exec ./{TimerPath.DismissFileName} at the timers "
        + "root. One call covers every satellite and every channel of the person's, and both kinds, whatever set the alert off, so "
        + "it is the whole of a silencing request — run it once and answer; there is no calendar "
        + "or home entity to visit afterwards. Not a shell — anything else returns exit 127.";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _parseOptions = new() { PropertyNameCaseInsensitive = true };

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        const string action = "/" + TimerPath.DismissFileName;
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        if (_household is not { } household)
        {
            return Nobody<FsGlobResult>();
        }

        var (dirsOnly, matches) = scope;
        var all = await store.ListAsync(household, ct);

        var dirs = all.Select(t => t.Id).Where(matches).Select(id => $"/{id}/");
        if (dirsOnly)
        {
            return Glob(pattern, () => dirs.OrderBy(p => p, StringComparer.Ordinal).ToList());
        }

        var files = all.SelectMany(t => new[]
            {
                $"{t.Id}/{TimerPath.TimerFileName}",
                $"{t.Id}/{TimerPath.StatusFileName}"
            })
            .Concat([TimerPath.DismissFileName, TimerPath.SatellitesFileName])
            .Where(matches)
            .Select(p => $"/{p}");
        return Glob(
            pattern,
            () => dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList(),
            entry => entry == action);
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        if (_household is null)
        {
            return Nobody<FsInfoResult>();
        }

        var node = TimerPath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is TimerNodeKind.Root or TimerNodeKind.TimerDir;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null,
            Executable = node.Kind == TimerNodeKind.DismissFile
        });
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        if (_household is not { } household)
        {
            return Nobody<FsReadResult>();
        }

        var node = TimerPath.Parse(path);
        string content;
        switch (node.Kind)
        {
            case TimerNodeKind.SatellitesFile:
                content = await RenderSatellitesAsync(household, ct);
                break;
            case TimerNodeKind.TimerFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderSpec(t);
                break;
            case TimerNodeKind.StatusFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderStatus(t, await _householdOfCall.ZoneAsync(timeProvider.LocalTimeZone, ct));
                break;
            case TimerNodeKind.DismissFile:
                return ExecutableOnly<FsReadResult>(path, TimerPath.DismissFileName);
            default:
                return NotFound<FsReadResult>(path);
        }

        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    public override async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        if (_household is not { } household)
        {
            return Nobody<FsSearchResult>();
        }

        var scope = path ?? directoryPath;

        if (!CompileFilePattern(filePattern).TryGetValue(out var admits, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        // timer.json is the only searchable file per timer, so a filePattern either includes it
        // (search the scoped timers) or excludes it entirely (nothing to search).
        var scoped = admits(TimerPath.TimerFileName)
            ? await ScopeTimersAsync(household, scope, ct)
            : [];

        return await SearchNodesAsync(
            scoped,
            (timer, _) => ValueTask.FromResult<(string, string?)>(
                ($"/{timer.Id}/{TimerPath.TimerFileName}", RenderSpec(timer))),
            new FsSearchScan
            {
                Query = query,
                Regex = regex,
                Path = scope ?? "/",
                MaxResults = maxResults,
                ContextLines = contextLines,
                OutputMode = outputMode
            },
            ct);
    }

    public override async Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (_household is not { } household)
        {
            return Nobody<FsCreateResult>();
        }

        var node = TimerPath.Parse(path);
        if (node.Kind == TimerNodeKind.DismissFile)
        {
            return ExecutableOnly<FsCreateResult>(path, TimerPath.DismissFileName);
        }

        if (node.Kind == TimerNodeKind.SatellitesFile)
        {
            return ReadOnly<FsCreateResult>(path);
        }

        // A timer directory holds one writable file, so a body written to /<id> can only mean it.
        // A segment with an extension is a file spelled wrong, not a directory, and stays refused.
        if (node.Kind == TimerNodeKind.TimerDir && !node.TimerId!.Contains('.'))
        {
            path = $"/{node.TimerId}/{TimerPath.TimerFileName}";
            node = TimerPath.Parse(path);
        }

        if (node.Kind != TimerNodeKind.TimerFile || node.TimerId is null)
        {
            return Invalid<FsCreateResult>($"Create a timer at /<timerId>/{TimerPath.TimerFileName} (got '{path}')");
        }

        // Timers are immutable: create always rejects an existing id regardless of `overwrite`.
        if (await store.GetAsync(household, node.TimerId, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(
                Error(ToolError.Codes.AlreadyExists, $"Timer '{node.TimerId}' already exists"));
        }

        var spec = ParseSpec(content, out var specError);
        if (specError is not null)
        {
            return new FsResult<FsCreateResult>.Err(specError);
        }

        ToolErrorResult? validation;
        try
        {
            validation = await ValidateSpec(household, spec!, ct);
        }
        catch (VoiceHubUnavailableException)
        {
            // A household whose voice server is not connected can still have the timer on its
            // person's channels, and the refusal is where the model learns that it may.
            return HubUnavailable<FsCreateResult>("the timer was not armed", _owner is null
                ? null
                : "Or leave target out to ring it on the person's own channels instead.");
        }
        if (validation is not null)
        {
            return new FsResult<FsCreateResult>.Err(validation);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = node.TimerId,
            TenantId = household,
            Owner = _owner,
            AgentId = _agentId,
            Text = spec!.Text,
            Target = spec.Target,
            DurationSeconds = spec.DurationSeconds!.Value,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(spec.DurationSeconds.Value)
        }, ct);

        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = content.Length.ToString(), Lines = content.Split('\n').Length
        });
    }

    // No EditAsync override: a timer is immutable, so an edit here could only ever fail, and
    // capability is declared by overriding — advertising fs_edit would promise an operation this
    // mount does not have.

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        if (_household is not { } household)
        {
            return Nobody<FsRemoveResult>();
        }

        var node = TimerPath.Parse(path);
        if (node.Kind == TimerNodeKind.DismissFile)
        {
            return ExecutableOnly<FsRemoveResult>(path, TimerPath.DismissFileName);
        }

        if (node.Kind == TimerNodeKind.SatellitesFile)
        {
            return ReadOnly<FsRemoveResult>(path);
        }
        if (node.Kind != TimerNodeKind.TimerDir)
        {
            return node.Kind is TimerNodeKind.TimerFile or TimerNodeKind.StatusFile
                   && await GetTimerAsync(node, ct) is not null
                ? Invalid<FsRemoveResult>($"Cancel the timer by deleting its directory: /{node.TimerId}")
                : NotFound<FsRemoveResult>(path);
        }

        return await store.CancelAsync(household, node.TimerId!, ct)
            ? new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "deleted", Message = "cancelled", OriginalPath = path, TrashPath = ""
            })
            : NotFound<FsRemoveResult>(path);
    }

    public override async Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        if (_household is not { } household)
        {
            return Nobody<FsExecResult>();
        }

        var node = TimerPath.Parse(path);
        if (node.Kind is not (TimerNodeKind.Root or TimerNodeKind.DismissFile))
        {
            return Fail<FsExecResult>(ToolError.Codes.UnsupportedOperation,
                $"exec is only supported at the timers root: exec ./{TimerPath.DismissFileName}");
        }

        var trimmed = WithoutDotSlash(command.Trim());

        // A refusal that names only the whole command line cannot say whether the script or its
        // argument was wrong, and a model that guessed a flag reads it as the script being
        // elsewhere and goes looking. Answer the two cases separately.
        if (trimmed != TimerPath.DismissFileName)
        {
            var script = trimmed.Split(' ', 2)[0];

            return Exec(
                "",
                script == TimerPath.DismissFileName
                    ? $"{TimerPath.DismissFileName} takes no arguments: it already silences every "
                      + "alert on every satellite. Run it on its own."
                    : $"command not found: {script}\navailable: {TimerPath.DismissFileName}",
                127,
                path);
        }

        // What rings on the household's people's channels is stopped whether or not its voice server
        // can be reached: a household with none still has timers, and those are the whole of it.
        var onChannels = await alerts.StopAllAsync(household, ct);
        IReadOnlyList<DismissedAlert> dismissed;
        try
        {
            dismissed = [.. await voices.For(household).Dismisser.DismissAllAsync(ct), .. onChannels];
        }
        catch (VoiceHubUnavailableException) when (onChannels.Count == 0)
        {
            // A household with no voice server lands here every time nothing rings, and the first
            // half of the sentence is its whole answer; a household whose server is down learns the
            // satellites could not be reached.
            return HubUnavailable<FsExecResult>(
                "nothing ringing on a satellite was dismissed; nothing is ringing on the household's "
                + "chat clients or Telegram",
                "A household with no satellites has nothing more to silence.");
        }
        catch (VoiceHubUnavailableException)
        {
            dismissed = onChannels;
        }
        // Both lines end the same way on purpose. "nothing is ringing" alone reads as a failure
        // to find the alert rather than as the silence itself, and a model that cannot tell those
        // apart goes looking for what was ringing somewhere else — through /ha, in practice. This
        // call silences everything on every satellite in one go, so there is never a second place
        // to look and never a reason to run it twice.
        var what = dismissed.Count == 0
            ? "nothing is ringing"
            : "dismissed " + string.Join(
                " and ", dismissed.Select(d => $"{d.Kind.ToString().ToLowerInvariant()} \"{d.Text}\""));

        return Exec($"{what}; nothing further to do\n", "", 0, path);
    }

    private sealed record SpecDto
    {
        public int? DurationSeconds { get; init; }
        public string? Text { get; init; }
        public AnnounceTarget? Target { get; init; }
    }

    private static SpecDto? ParseSpec(string content, out ToolErrorResult? error)
    {
        error = null;
        try
        {
            var spec = JsonSerializer.Deserialize<SpecDto>(content, _parseOptions);
            if (spec is null)
            {
                error = Error(ToolError.Codes.InvalidArgument, "timer.json is empty");
            }
            return spec;
        }
        catch (JsonException ex)
        {
            error = Error(ToolError.Codes.InvalidArgument, $"Invalid timer.json: {ex.Message}");
            return null;
        }
    }

    // Kitchen-scale countdowns in a deliberately non-durable store: anything longer belongs on the
    // HA alarms calendar, which survives restarts and escalates.
    public const int MaxDurationSeconds = 4 * 60 * 60;

    private async Task<ToolErrorResult?> ValidateSpec(string household, SpecDto spec, CancellationToken ct)
    {
        if (spec.DurationSeconds is not > 0)
        {
            return Error(ToolError.Codes.InvalidArgument, "durationSeconds must be a positive integer");
        }

        if (spec.DurationSeconds > MaxDurationSeconds)
        {
            return Error(ToolError.Codes.InvalidArgument,
                $"durationSeconds must be at most {MaxDurationSeconds} (4 hours) — use the Home Assistant "
                + "alarms calendar for anything longer");
        }

        // A blank (non-null) text bypasses the "<id> timer" fallback and the announcer rejects it at
        // fire time, so the timer would never ring. Omitting text is fine — it auto-names.
        if (spec.Text is not null && string.IsNullOrWhiteSpace(spec.Text))
        {
            return Error(ToolError.Codes.InvalidArgument,
                "text must not be blank — omit it to auto-name the timer");
        }

        // No target rings the timer on its owner's channels, which needs an owner: the household's
        // guest has no chat client or Telegram of its own to ring on.
        var target = spec.Target;
        if (target is null)
        {
            return _owner is null
                ? Error(ToolError.Codes.InvalidArgument,
                    "target is required for a turn acting for no account: a timer with no target rings on "
                    + "its owner's own channels, and the household's guest has none. Give it a "
                    + "target: {satelliteId | satelliteIds | room | all}")
                : null;
        }

        if (target.SatelliteId is null
            && target.SatelliteIds is not { Count: > 0 }
            && target.Room is null
            && target.All != true)
        {
            return Error(ToolError.Codes.InvalidArgument,
                "target must name satellites: {satelliteId | satelliteIds | room | all}, or be left out "
                + "to ring on the person's own channels");
        }

        var satellites = voices.For(household).Satellites;

        // A target the announcer can't resolve arms a timer that never rings: by fire time the
        // timer has already been claimed out of the store and the failure is only logged. Reject
        // it here instead, naming the satellites so the agent can retry with a real one. The roster
        // is fetched once and reused for both the unknown-id check and the error message.
        var roster = await satellites.GetAllAsync(ct);
        var known = roster.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> named = target.SatelliteIds is { Count: > 0 }
            ? target.SatelliteIds.Where(id => id is not null)
            : target.SatelliteId is not null ? [target.SatelliteId] : [];
        var unknown = named.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            return Error(ToolError.Codes.NotFound,
                $"unknown satellite(s): {string.Join(", ", unknown)}. Known satellites: {Describe(roster)}");
        }

        return (await satellites.ResolveAsync(target, ct)).Count > 0
            ? null
            : Error(ToolError.Codes.NotFound,
                $"target matches no satellite. Known satellites: {Describe(roster)}");
    }

    private static string Describe(IReadOnlyList<SatelliteDescriptor> roster) =>
        string.Join(", ", roster.Select(s => $"{s.Id} (room \"{s.Room}\")"));

    // The household's satellites as its voice server answers for them now. One that is not connected
    // has nowhere to ring a timer but its people's channels, and says so rather than failing a read.
    private async Task<string> RenderSatellitesAsync(string household, CancellationToken ct)
    {
        try
        {
            var roster = await voices.For(household).Satellites.GetAllAsync(ct);
            return JsonSerializer.Serialize(new
            {
                satellites = roster.Select(s => new { id = s.Id, room = s.Room })
            }, _json);
        }
        catch (VoiceHubUnavailableException)
        {
            return JsonSerializer.Serialize(new
            {
                satellites = Array.Empty<object>(),
                note = "the household's voice server is not connected, so no satellite can ring now; "
                       + "a timer with no target rings on the person's own channels"
            }, _json);
        }
    }

    private string RenderSpec(ArmedTimer t) => JsonSerializer.Serialize(new
    {
        durationSeconds = t.DurationSeconds,
        text = t.Text,
        target = t.Target
    }, _json);

    private string RenderStatus(ArmedTimer t, TimeZoneInfo zone)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return JsonSerializer.Serialize(new
        {
            remainingSeconds = Math.Max(0, (int)Math.Ceiling((t.FiresAtUtc - now).TotalSeconds)),
            firesAt = ToZone(t.FiresAtUtc, zone)
        }, _json);
    }

    // Stored times are UTC; render them in the calling household's zone (the server's for a call that
    // proves none) so the model reads the household's wall clock.
    private static DateTimeOffset ToZone(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), zone);

    private async Task<ArmedTimer?> GetTimerAsync(TimerNode node, CancellationToken ct) =>
        node.TimerId is null || _household is null ? null : await store.GetAsync(_household, node.TimerId, ct);

    private async Task<bool> NodeExistsAsync(TimerNode node, CancellationToken ct) => node.Kind switch
    {
        TimerNodeKind.Root or TimerNodeKind.DismissFile or TimerNodeKind.SatellitesFile => true,
        TimerNodeKind.TimerDir or TimerNodeKind.TimerFile or TimerNodeKind.StatusFile =>
            await GetTimerAsync(node, ct) is not null,
        _ => false
    };

    private async Task<IReadOnlyList<ArmedTimer>> ScopeTimersAsync(string household, string? scope, CancellationToken ct)
    {
        var all = await store.ListAsync(household, ct);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return all;
        }

        var node = TimerPath.Parse(scope);
        return node.Kind switch
        {
            TimerNodeKind.Root => all,
            TimerNodeKind.TimerDir or TimerNodeKind.TimerFile or TimerNodeKind.StatusFile =>
                all.Where(t => t.Id == node.TimerId).ToList(),
            _ => []
        };
    }

    private static FsResult<FsExecResult> Exec(string stdout, string stderr, int exitCode, string cwd) =>
        new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = stdout, Stderr = stderr, ExitCode = exitCode,
            Truncated = false, TimedOut = false, DurationMs = 0, Cwd = cwd
        });

    private const string ForNobody =
        "This call acts for nobody, so it has no household and no timers: /timers answers for the "
        + "household of the person or guest whose turn it is.";

    private static FsResult<T> Nobody<T>() where T : class => Fail<T>(ToolError.Codes.PermissionDenied, ForNobody);

    private static FsResult<T> HubUnavailable<T>(string consequence, string? otherwise = null) where T : class =>
        new FsResult<T>.Err(Error(
            ToolError.Codes.TransientDependency,
            $"The voice hub is unreachable, so {consequence}.",
            "The hub is what reaches the satellites; the same call works once it answers again."
            + (otherwise is null ? "" : " " + otherwise)));

    private static ToolErrorResult Error(string code, string message, string? hint = null) =>
        new() { ErrorCode = code, Message = message, Hint = hint };
}