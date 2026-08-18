using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Scheduling.Vfs;

public sealed class ScheduleFileSystem(
    IScheduleStore store,
    IAgentCatalog agents,
    ICronValidator cronValidator,
    TimeProvider timeProvider,
    TimeSpan? regexMatchTimeout = null) : FileSystemBackendBase
{
    public const string Name = "schedules";

    public override string FilesystemName => Name;

    protected override TimeSpan SearchMatchTimeout => regexMatchTimeout ?? base.SearchMatchTimeout;

    public override string DescribeMount => BuildMountDescription(timeProvider.LocalTimeZone.Id);

    // The words the model reads about each operation, next to the behaviour they describe. They
    // name the mount's real files, which is what makes the schedules surface usable without a probe.
    public override string DescribeGlob =>
        "Lists schedule filesystem entries matching a glob under basePath. `*` matches one "
        + "path segment, `**` recurses, `?` one char, `{a,b}` brace alternation. A trailing slash "
        + "(e.g. `*/`) lists directories only; otherwise files and directories both match, with "
        + "directory results marked by a trailing slash.";

    public override string DescribeInfo => "Get info about a schedule filesystem path";

    public override string DescribeRead =>
        "Read a schedule filesystem file (schedule.json/status.json/agent_info.json/run_now.sh)";

    public override string DescribeSearch =>
        "Searches schedule.json content across schedules. Scope with directoryPath (e.g. /<agentId>) "
        + "or path (a single schedule); omit both to search every schedule. Supports regex, "
        + "filePattern, maxResults, contextLines, and outputMode (content|filesOnly) like the other "
        + "filesystems.";

    public override string DescribeCreate =>
        "Create a schedule: fs_create /<agentId>/<descriptive-id>/schedule.json with JSON "
        + "{prompt, cron|runAt, userId?, deliverTo?}";

    public override string DescribeEdit => "Edit a schedule.json (prompt/timing/deliverTo)";

    public override string DescribeDelete => "Delete a schedule directory";

    public override string DescribeMove =>
        "Reassign a schedule to another agent or rename it: move /<agent>/<id> to /<agent2>/<id2>";

    public override string DescribeExec =>
        "Run a schedule action. path is the schedule DIRECTORY (e.g. /jonas/my-schedule); command "
        + "is 'run_now.sh' to fire it immediately. Not a shell — anything other than run_now.sh "
        + "returns exit 127.";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions _parseOptions = new() { PropertyNameCaseInsensitive = true };

    // Glob matches the same semantics as every other filesystem: the pattern (relative to basePath)
    // filters the schedule tree, `*`/`**`/`?`/`{a,b}` behave as documented, a trailing slash lists
    // directories only, and directory results are marked with a trailing slash.
    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        var (dirsOnly, matches) = scope;
        var all = await store.ListAsync(ct);

        var dirs = ScheduleTree.Directories(agents, all).Where(matches).Select(p => $"/{p}/");
        if (dirsOnly)
        {
            return Glob(pattern, () => dirs.OrderBy(p => p, StringComparer.Ordinal).ToList());
        }

        var files = ScheduleTree.Files(agents, all).Where(matches).Select(p => $"/{p}");
        return Glob(pattern, () => dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is ScheduleNodeKind.Root or ScheduleNodeKind.AgentDir or ScheduleNodeKind.ScheduleDir;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null
        });
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        // offset/limit are unused: schedule files are small JSON blobs, always returned whole (Truncated = false).
        string content;
        switch (node.Kind)
        {
            case ScheduleNodeKind.AgentInfoFile when agents.Get(node.AgentId!) is { } info:
                content = JsonSerializer.Serialize(info, _json);
                break;
            case ScheduleNodeKind.ScheduleFile when await GetScheduleAsync(node, ct) is { } s:
                content = RenderSpec(s);
                break;
            case ScheduleNodeKind.StatusFile when await GetScheduleAsync(node, ct) is { } s:
                content = RenderStatus(s);
                break;
            case ScheduleNodeKind.RunNowFile when await GetScheduleAsync(node, ct) is not null:
                content = "# Run this schedule now:\n#   exec run_now.sh\n";
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

    // fs_search follows the standard VFS convention: it scans each schedule's searchable schedule.json
    // content line-by-line, honoring regex, scope (path/directoryPath), filePattern, maxResults,
    // contextLines, and the Content/FilesOnly output shape — identical to the file-backed backends.
    public override async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        var scope = path ?? directoryPath;

        if (!CompileFilePattern(filePattern).TryGetValue(out var admits, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        // schedule.json is the only searchable file per schedule, so a filePattern either includes it
        // (search the scoped schedules) or excludes it entirely (nothing to search).
        var scoped = admits(SchedulePath.ScheduleFileName)
            ? await ScopeSchedulesAsync(scope, ct)
            : [];

        return await SearchNodesAsync(
            scoped,
            (schedule, _) => ValueTask.FromResult<(string, string?)>(
                ($"/{schedule.AgentId}/{schedule.Id}/{SchedulePath.ScheduleFileName}", RenderSpec(schedule))),
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

    // Restricts the searched set to the requested scope: a single schedule (file/dir path), one
    // agent's schedules (agent dir), or everything (root / null). An unknown path scopes to nothing.
    private async Task<IReadOnlyList<Schedule>> ScopeSchedulesAsync(string? scope, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return all;
        }

        var node = SchedulePath.Parse(scope);
        return node.Kind switch
        {
            ScheduleNodeKind.Root => all,
            ScheduleNodeKind.AgentDir when agents.Exists(node.AgentId!) =>
                all.Where(s => s.AgentId == node.AgentId).ToList(),
            ScheduleNodeKind.ScheduleDir or ScheduleNodeKind.ScheduleFile
                or ScheduleNodeKind.StatusFile or ScheduleNodeKind.RunNowFile =>
                all.Where(s => s.AgentId == node.AgentId && s.Id == node.ScheduleId).ToList(),
            _ => []
        };
    }

    private string RenderSpec(Schedule s) => JsonSerializer.Serialize(new
    {
        prompt = s.Prompt,
        cron = s.CronExpression,
        runAt = ToZone(s.RunAt),
        userId = s.UserId,
        deliverTo = s.DeliverTo
    }, _json);

    private string RenderStatus(Schedule s) => JsonSerializer.Serialize(new
    {
        createdAt = ToZone(s.CreatedAt),
        lastRunAt = ToZone(s.LastRunAt),
        nextRunAt = ToZone(s.NextRunAt)
    }, _json);

    private async Task<Schedule?> GetScheduleAsync(ScheduleNode node, CancellationToken ct)
    {
        if (node.AgentId is null || node.ScheduleId is null || !agents.Exists(node.AgentId))
        {
            return null;
        }

        var s = await store.GetAsync(node.ScheduleId, ct);
        return s is not null && s.AgentId == node.AgentId ? s : null;
    }

    private async Task<bool> NodeExistsAsync(ScheduleNode node, CancellationToken ct) => node.Kind switch
    {
        ScheduleNodeKind.Root => true,
        ScheduleNodeKind.AgentDir or ScheduleNodeKind.AgentInfoFile => agents.Exists(node.AgentId!),
        ScheduleNodeKind.ScheduleDir or ScheduleNodeKind.ScheduleFile
            or ScheduleNodeKind.StatusFile or ScheduleNodeKind.RunNowFile => await GetScheduleAsync(node, ct) is not null,
        _ => false
    };

    public override async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleFile || node.AgentId is null || node.ScheduleId is null)
        {
            return Invalid<FsCreateResult>($"Create a schedule at /<agentId>/<scheduleId>/schedule.json (got '{path}')");
        }

        if (!agents.Exists(node.AgentId))
        {
            return new FsResult<FsCreateResult>.Err(Error(ToolError.Codes.NotFound, $"Unknown agent '{node.AgentId}'"));
        }

        // Schedules use a unique-id model: create always rejects an existing id regardless of `overwrite`. Use fs_edit to modify an existing schedule.
        if (await store.GetAsync(node.ScheduleId, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(Error(ToolError.Codes.AlreadyExists, $"Schedule '{node.ScheduleId}' already exists"));
        }

        var spec = ParseSpec(content, out var specError);
        if (specError is not null)
        {
            return new FsResult<FsCreateResult>.Err(specError);
        }

        var validation = ValidateSpec(spec!);
        if (validation is not null)
        {
            return new FsResult<FsCreateResult>.Err(validation);
        }

        spec = spec! with { RunAt = spec.RunAt is { } r ? ToUtc(r) : null };

        var schedule = new Schedule
        {
            Id = node.ScheduleId,
            AgentId = node.AgentId,
            Prompt = spec!.Prompt!,
            CronExpression = spec.Cron,
            RunAt = spec.RunAt,
            UserId = spec.UserId,
            DeliverTo = spec.DeliverTo,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            NextRunAt = ComputeNextRunAt(spec)
        };

        await store.CreateAsync(schedule, ct);
        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = content.Length.ToString(), Lines = content.Split('\n').Length
        });
    }

    public override async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleFile)
        {
            return await RejectWriteAsync<FsEditResult>(node, path, ct);
        }

        if (await GetScheduleAsync(node, ct) is not { } existing)
        {
            return NotFound<FsEditResult>(path);
        }

        var current = RenderSpec(existing);
        var updatedText = edits.Aggregate(current, (acc, e) =>
            e.ReplaceAll ? acc.Replace(e.OldString, e.NewString)
                         : ReplaceFirst(acc, e.OldString, e.NewString));

        var spec = ParseSpec(updatedText, out var specError);
        if (specError is not null)
        {
            return new FsResult<FsEditResult>.Err(specError);
        }

        var validation = ValidateSpec(spec!);
        if (validation is not null)
        {
            return new FsResult<FsEditResult>.Err(validation);
        }

        spec = spec! with { RunAt = spec.RunAt is { } r ? ToUtc(r) : null };

        // Only recompute the next fire when the timing actually changes; a prompt-only edit must
        // not push out (or skip) an already-scheduled run by recomputing NextRunAt from "now".
        var timingChanged = spec.Cron != existing.CronExpression || spec.RunAt != existing.RunAt;

        var updated = existing with
        {
            Prompt = spec.Prompt!,
            CronExpression = spec.Cron,
            RunAt = spec.RunAt,
            UserId = spec.UserId,
            DeliverTo = spec.DeliverTo,
            NextRunAt = timingChanged ? ComputeNextRunAt(spec) : existing.NextRunAt
        };

        await store.CreateAsync(updated, ct);
        return new FsResult<FsEditResult>.Ok(new FsEditResult
        {
            Status = "edited", FilePath = path, TotalOccurrencesReplaced = edits.Count,
            Edits = edits.Select(_ => new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }).ToList()
        });
    }

    public override async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        var src = SchedulePath.Parse(sourcePath);
        var dst = SchedulePath.Parse(destinationPath);
        if (src.Kind != ScheduleNodeKind.ScheduleDir || dst.Kind != ScheduleNodeKind.ScheduleDir)
        {
            return IsReadOnlyFile(src.Kind) && await NodeExistsAsync(src, ct)
                ? ReadOnly<FsMoveResult>(sourcePath)
                : Invalid<FsMoveResult>("Move a schedule dir to /<agentId>/<scheduleId>");
        }

        if (await GetScheduleAsync(src, ct) is not { } existing)
        {
            return NotFound<FsMoveResult>(sourcePath);
        }

        if (!agents.Exists(dst.AgentId!))
        {
            return new FsResult<FsMoveResult>.Err(Error(ToolError.Codes.NotFound, $"Unknown agent '{dst.AgentId}'"));
        }

        if (dst.ScheduleId != src.ScheduleId && await store.GetAsync(dst.ScheduleId!, ct) is not null)
        {
            return new FsResult<FsMoveResult>.Err(Error(ToolError.Codes.AlreadyExists, $"Schedule '{dst.ScheduleId}' already exists"));
        }

        if (dst.ScheduleId != src.ScheduleId)
        {
            await store.DeleteAsync(src.ScheduleId!, ct);
        }

        await store.CreateAsync(existing with { Id = dst.ScheduleId!, AgentId = dst.AgentId! }, ct);
        return new FsResult<FsMoveResult>.Ok(new FsMoveResult
        {
            Status = "moved", Message = "reassigned", Source = sourcePath, Destination = destinationPath
        });
    }

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleDir)
        {
            return await RejectWriteAsync<FsRemoveResult>(node, path, ct);
        }

        if (await GetScheduleAsync(node, ct) is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        await store.DeleteAsync(node.ScheduleId!, ct);
        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "deleted", Message = "removed", OriginalPath = path, TrashPath = ""
        });
    }

    public override async Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind != ScheduleNodeKind.ScheduleDir || await GetScheduleAsync(node, ct) is not { } schedule)
        {
            return NotFound<FsExecResult>(path);
        }

        var trimmed = command.Trim();
        if (trimmed != SchedulePath.RunNowFileName)
        {
            return Exec("", $"command not found: {trimmed}\navailable: {SchedulePath.RunNowFileName}", 127, path);
        }

        // Queue the schedule for the dispatcher's next tick by setting NextRunAt=now. LastRunAt is left
        // untouched (null = don't change it); the dispatcher stamps the real fire-time when it actually runs.
        await store.UpdateLastRunAsync(schedule.Id, null, timeProvider.GetUtcNow().UtcDateTime, ct);
        return Exec($"queued '{schedule.Id}' to run now\n", "", 0, path);
    }

    private static FsResult<FsExecResult> Exec(string stdout, string stderr, int exitCode, string cwd) =>
        new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = stdout, Stderr = stderr, ExitCode = exitCode,
            Truncated = false, TimedOut = false, DurationMs = 0, Cwd = cwd
        });

    private sealed record SpecDto
    {
        public string? Prompt { get; init; }
        public string? Cron { get; init; }
        public DateTime? RunAt { get; init; }
        public string? UserId { get; init; }
        public IReadOnlyList<string>? DeliverTo { get; init; }
    }

    private static SpecDto? ParseSpec(string content, out ToolErrorResult? error)
    {
        error = null;
        try
        {
            var spec = JsonSerializer.Deserialize<SpecDto>(content, _parseOptions);
            if (spec is null)
            {
                error = Error(ToolError.Codes.InvalidArgument, "schedule.json is empty");
            }

            return spec;
        }
        catch (JsonException ex)
        {
            error = Error(ToolError.Codes.InvalidArgument, $"Invalid schedule.json: {ex.Message}");
            return null;
        }
    }

    private ToolErrorResult? ValidateSpec(SpecDto spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Prompt))
        {
            return Error(ToolError.Codes.InvalidArgument, "prompt is required");
        }

        if (spec.Cron is null && spec.RunAt is null)
        {
            return Error(ToolError.Codes.InvalidArgument, "Provide either cron or runAt");
        }

        if (spec.Cron is not null && spec.RunAt is not null)
        {
            return Error(ToolError.Codes.InvalidArgument, "Provide only cron OR runAt, not both");
        }

        if (spec.Cron is not null && !cronValidator.IsValid(spec.Cron))
        {
            return Error(ToolError.Codes.InvalidArgument, $"Invalid cron expression: {spec.Cron}");
        }

        if (spec.RunAt is { } runAt)
        {
            if (runAt.Kind == DateTimeKind.Unspecified && timeProvider.LocalTimeZone.IsInvalidTime(runAt))
            {
                return Error(ToolError.Codes.InvalidArgument,
                    "runAt falls in a daylight-saving gap (that local time does not exist); pick another time or add an explicit offset");
            }

            if (ToUtc(runAt) <= timeProvider.GetUtcNow().UtcDateTime)
            {
                return Error(ToolError.Codes.InvalidArgument, "runAt must be in the future");
            }
        }

        return null;
    }

    // A bare (zoneless) runAt is wall-clock time in the operating zone; an offset/Z runAt is honored.
    private DateTime ToUtc(DateTime runAt) =>
        runAt.Kind == DateTimeKind.Unspecified
            ? TimeZoneInfo.ConvertTimeToUtc(runAt, timeProvider.LocalTimeZone)
            : runAt.ToUniversalTime();

    // Stored times are UTC; render them in the operating zone so the LLM reads local wall-clock.
    private DateTimeOffset? ToZone(DateTime? utc) =>
        utc is { } u
            ? TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)), timeProvider.LocalTimeZone)
            : null;

    private DateTime? ComputeNextRunAt(SpecDto spec) =>
        spec.RunAt ?? (spec.Cron is not null
            ? cronValidator.GetNextOccurrence(spec.Cron, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone)
            : null);

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return i < 0 ? text : text[..i] + newValue + text[(i + oldValue.Length)..];
    }

    private static bool IsReadOnlyFile(ScheduleNodeKind kind) =>
        kind is ScheduleNodeKind.StatusFile or ScheduleNodeKind.AgentInfoFile or ScheduleNodeKind.RunNowFile;

    // A write aimed at a path that isn't a writable schedule.json is either a known read-only file
    // (status.json/agent_info.json/run_now.sh) that exists — rejected as read-only — or a genuine miss.
    private async Task<FsResult<T>> RejectWriteAsync<T>(ScheduleNode node, string path, CancellationToken ct) where T : class =>
        IsReadOnlyFile(node.Kind) && await NodeExistsAsync(node, ct) ? ReadOnly<T>(path) : NotFound<T>(path);

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };

    // The zone the engine actually computes in, read off the injected TimeProvider rather than a
    // static call, so what the model is told and what a cron expression means cannot drift apart.
    private static string BuildMountDescription(string zone) =>
        $$"""Scheduled agent tasks, grouped by agent. Discover agents by globbing /schedules (each agent is a directory); read /schedules/<agentId>/agent_info.json to learn what another agent does. Schedule against yourself — the agent directory whose agent_info.json name is your own — unless the user names another agent: the directory you write to decides who runs the prompt and where the result is delivered, so another agent's directory means someone else does the work and answers on their own channel. Create a schedule with fs_create at /schedules/<agentId>/<descriptive-unique-id>/schedule.json containing JSON {prompt, cron|runAt, userId?, deliverTo?}: provide EXACTLY ONE of cron (recurring, standard 5-field cron read in the {{zone}} time zone and adjusted automatically across daylight-saving changes, e.g. "0 9 * * *" = daily 09:00, "30 14 * * 1-5" = weekdays 14:30) or runAt (one-shot ISO-8601 datetime; give it a time zone — 'Z' for UTC or an offset like +02:00 — or omit one and it is read as {{zone}} local time; stored as UTC, auto-deleted after it fires). deliverTo is an optional list of channel ids (e.g. ["signalr","telegram"]) to receive the result; omit for the default. Change prompt/timing with fs_edit, reassign to another agent or rename with fs_move, remove with fs_delete. Read /schedules/<agentId>/<scheduleId>/status.json for createdAt/lastRunAt/nextRunAt, shown in the {{zone}} time zone. Fire a schedule immediately with fs_exec on its directory using command run_now.sh. Use descriptive, unique schedule ids.""";

}