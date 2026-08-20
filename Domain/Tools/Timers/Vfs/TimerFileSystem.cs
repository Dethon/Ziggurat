using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Tools;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Timers.Vfs;

// Hub-local countdown timers as a VFS: create /<id>/timer.json to arm, read status.json for time
// left, delete the directory to cancel. Timers are immutable (delete and recreate) and fire once.
// /dismiss.sh (exec) silences every alert currently ringing — alarms and timers alike — so "stop
// the alarm" works from any room or channel, not just by waking a targeted satellite.
public sealed class TimerFileSystem(
    ITimerStore store, TimeProvider timeProvider, IAlertDismisser dismisser,
    ISatelliteCatalog satellites, TimeSpan? regexMatchTimeout = null) : FileSystemBackendBase
{
    public const string Name = "timers";

    public override string FilesystemName => Name;

    protected override TimeSpan SearchMatchTimeout => regexMatchTimeout ?? base.SearchMatchTimeout;

    public override string DescribeMount =>
        "Short countdown timers that ring on the voice satellites. Arm one by creating "
        + "/timers/<descriptive-id>/timer.json with JSON {durationSeconds, text?, target} — target "
        + "is {satelliteId | satelliteIds | room | all}; default it to the speaking room. Read "
        + "/timers/<id>/status.json for remainingSeconds/firesAt; cancel by deleting /timers/<id>. "
        + "Timers are immutable (delete and recreate) and fire once, ringing tone + message until "
        + "dismissed by wake word/button or capped. Exec dismiss.sh at /timers to silence "
        + "everything currently ringing (alarms and timers) from any room or channel. Use the HA "
        + "alarms calendar for clock-time alarms/reminders, not timers.";

    // The words the model reads about each operation, next to the behaviour they describe. They
    // name the mount's real files, which is what makes the timers surface usable without a probe.
    public override string DescribeGlob =>
        "Lists timer filesystem entries matching a glob under basePath. `*` matches one "
        + "path segment, `**` recurses, `?` one char, `{a,b}` brace alternation. A trailing slash "
        + "(e.g. `*/`) lists directories only; otherwise files and directories both match, with "
        + "directory results marked by a trailing slash.";

    public override string DescribeInfo => "Get info about a timer filesystem path";

    public override string DescribeRead => "Read a timer filesystem file (timer.json/status.json)";

    public override string DescribeSearch =>
        "Searches timer.json content across timers. Scope with path (a single timer); omit to "
        + "search every timer. Supports regex, filePattern, maxResults, contextLines, and "
        + "outputMode (content|filesOnly) like the other filesystems.";

    public override string DescribeCreate =>
        "Arm a timer: fs_create /<descriptive-id>/timer.json with JSON {durationSeconds, text?, target}";

    public override string DescribeDelete => "Cancel a timer by deleting its directory /<timerId>";

    public override string DescribeExec =>
        $"Silence every alert currently ringing: exec {TimerPath.DismissFileName} at the timers "
        + "root. Not a shell — anything else returns exit 127.";

    private const string DismissHelp =
        "# Dismiss everything currently ringing (alarms and timers) on all satellites:\n"
        + "#   exec dismiss.sh\n";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _parseOptions = new() { PropertyNameCaseInsensitive = true };

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        var (dirsOnly, matches) = scope;
        var all = await store.ListAsync(ct);

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
            .Concat([TimerPath.DismissFileName])
            .Where(matches)
            .Select(p => $"/{p}");
        return Glob(pattern, () => dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is TimerNodeKind.Root or TimerNodeKind.TimerDir;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null
        });
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        string content;
        switch (node.Kind)
        {
            case TimerNodeKind.TimerFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderSpec(t);
                break;
            case TimerNodeKind.StatusFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderStatus(t);
                break;
            case TimerNodeKind.DismissFile:
                content = DismissHelp;
                break;
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
        var scope = path ?? directoryPath;

        if (!CompileFilePattern(filePattern).TryGetValue(out var admits, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        // timer.json is the only searchable file per timer, so a filePattern either includes it
        // (search the scoped timers) or excludes it entirely (nothing to search).
        var scoped = admits(TimerPath.TimerFileName)
            ? await ScopeTimersAsync(scope, ct)
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
        var node = TimerPath.Parse(path);
        if (node.Kind != TimerNodeKind.TimerFile || node.TimerId is null)
        {
            return Invalid<FsCreateResult>($"Create a timer at /<timerId>/{TimerPath.TimerFileName} (got '{path}')");
        }

        // Timers are immutable: create always rejects an existing id regardless of `overwrite`.
        if (await store.GetAsync(node.TimerId, ct) is not null)
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
            validation = await ValidateSpec(spec!, ct);
        }
        catch (VoiceHubUnavailableException)
        {
            return HubUnavailable<FsCreateResult>("the timer was not armed");
        }
        if (validation is not null)
        {
            return new FsResult<FsCreateResult>.Err(validation);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = node.TimerId,
            Text = spec!.Text,
            Target = spec.Target!,
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
        var node = TimerPath.Parse(path);
        if (node.Kind == TimerNodeKind.DismissFile)
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

        return await store.CancelAsync(node.TimerId!, ct)
            ? new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "deleted", Message = "cancelled", OriginalPath = path, TrashPath = ""
            })
            : NotFound<FsRemoveResult>(path);
    }

    public override async Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        if (node.Kind is not (TimerNodeKind.Root or TimerNodeKind.DismissFile))
        {
            return Fail<FsExecResult>(ToolError.Codes.UnsupportedOperation,
                $"exec is only supported at the timers root: exec {TimerPath.DismissFileName}");
        }

        var trimmed = command.Trim();
        if (node.Kind == TimerNodeKind.Root && trimmed != TimerPath.DismissFileName)
        {
            return Exec(
                "", $"command not found: {trimmed}\navailable: {TimerPath.DismissFileName}", 127, path);
        }

        IReadOnlyList<DismissedAlert> dismissed;
        try
        {
            dismissed = await dismisser.DismissAllAsync(ct);
        }
        catch (VoiceHubUnavailableException)
        {
            return HubUnavailable<FsExecResult>("nothing was dismissed");
        }
        var stdout = dismissed.Count == 0
            ? "nothing is ringing\n"
            : "dismissed " + string.Join(
                " and ", dismissed.Select(d => $"{d.Kind.ToString().ToLowerInvariant()} \"{d.Text}\"")) + "\n";
        return Exec(stdout, "", 0, path);
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

    private async Task<ToolErrorResult?> ValidateSpec(SpecDto spec, CancellationToken ct)
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

        var target = spec.Target;
        if (target is null
            || (target.SatelliteId is null
                && target.SatelliteIds is not { Count: > 0 }
                && target.Room is null
                && target.All != true))
        {
            return Error(ToolError.Codes.InvalidArgument,
                "target is required: {satelliteId | satelliteIds | room | all}");
        }

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

    private string RenderSpec(ArmedTimer t) => JsonSerializer.Serialize(new
    {
        durationSeconds = t.DurationSeconds,
        text = t.Text,
        target = t.Target
    }, _json);

    private string RenderStatus(ArmedTimer t)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return JsonSerializer.Serialize(new
        {
            remainingSeconds = Math.Max(0, (int)Math.Ceiling((t.FiresAtUtc - now).TotalSeconds)),
            firesAt = ToZone(t.FiresAtUtc)
        }, _json);
    }

    // Stored times are UTC; render them in the operating zone so the LLM reads local wall-clock.
    private DateTimeOffset ToZone(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), timeProvider.LocalTimeZone);

    private async Task<ArmedTimer?> GetTimerAsync(TimerNode node, CancellationToken ct) =>
        node.TimerId is null ? null : await store.GetAsync(node.TimerId, ct);

    private async Task<bool> NodeExistsAsync(TimerNode node, CancellationToken ct) => node.Kind switch
    {
        TimerNodeKind.Root or TimerNodeKind.DismissFile => true,
        TimerNodeKind.TimerDir or TimerNodeKind.TimerFile or TimerNodeKind.StatusFile =>
            await GetTimerAsync(node, ct) is not null,
        _ => false
    };

    private async Task<IReadOnlyList<ArmedTimer>> ScopeTimersAsync(string? scope, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
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

    private static FsResult<T> HubUnavailable<T>(string consequence) where T : class =>
        new FsResult<T>.Err(Error(
            ToolError.Codes.TransientDependency,
            $"The voice hub is unreachable, so {consequence}.",
            "The hub is what reaches the satellites; the same call works once it answers again."));

    private static ToolErrorResult Error(string code, string message, string? hint = null) =>
        new() { ErrorCode = code, Message = message, Hint = hint };
}