using System.Diagnostics;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem
{
    public override async Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        FsResult<FsExecResult> done(int exitCode, string stdout, string stderr, bool timedOut = false)
        {
            return ExecResult(exitCode, stdout, stderr, timedOut, sw.ElapsedMilliseconds, path);
        }

        var catalog = await catalogProvider.GetAsync(ct);
        var node = HaVfsPath.Parse(path);
        if (node.Kind != HaVfsKind.EntityDir)
        {
            return done(127, "", $"Not an entity directory: {path}. cd into /ha/entities/<class>/<id> first.");
        }

        var resolution = ResolveEntity(catalog, node);
        if (resolution.Entity is null)
        {
            var didYouMean = resolution.Hint is null
                ? ""
                : $" Did you mean '{resolution.Hint}'? Copy the exact name a listing returns.";
            return done(127, "", $"No such entity directory: {path}.{didYouMean}");
        }

        var tokens = ShellTokenize(command);
        var entityId = resolution.Entity.EntityId;
        var classDomain = HaCatalog.ClassOf(entityId);
        var actions = HaActionResolver.ServicesFor(resolution.Entity, catalog.Services);
        var available = string.Join(", ", actions.Select(a => $"{HaActionResolver.CommandName(a, classDomain)}.sh"));

        if (tokens.Count == 0)
        {
            return done(127, "", $"No command. Available actions: {available}");
        }

        var script = tokens[0].StartsWith("./", StringComparison.Ordinal) ? tokens[0][2..] : tokens[0];
        if (!script.EndsWith(".sh", StringComparison.Ordinal))
        {
            return done(127, "", $"command not found: {tokens[0]}. This filesystem only runs action files. Available actions: {available}");
        }

        var serviceName = script[..^3];
        var svc = actions.FirstOrDefault(a => HaActionResolver.CommandName(a, classDomain).Equals(serviceName, StringComparison.Ordinal));
        if (svc is null)
        {
            return done(127, "", $"command not found: {script}. Available actions: {available}");
        }

        var args = tokens.Skip(1).ToList();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            return done(0, HaServiceHelpRenderer.Render(entityId, svc), "");
        }

        JsonObject data;
        try
        {
            data = HaArgParser.Parse(args, svc, serviceName);
        }
        catch (ArgumentException ex)
        {
            return done(2, "", ex.Message);
        }

        NormalizeMediaSeek(svc, data);

        using var timeoutCts = timeoutSeconds is > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        timeoutCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds!.Value));
        var effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            // Served here: the recorder has no service, only a REST read, and every entity has a past.
            if (HaHistoryActions.IsHistory(svc))
            {
                var (code, output, error) = await HaHistory.RunAsync(clientFactory(), entityId, data, _time, effectiveCt);
                return done(code, output, error);
            }

            // Served here: long-term statistics are a WebSocket command and nothing else.
            if (HaStatisticsActions.IsStatistics(svc))
            {
                var (code, output, error) = await HaStatistics.RunAsync(clientFactory(), entityId, data, _time, effectiveCt);
                return done(code, output, error);
            }

            // Served here, not by HA: no Home Assistant call can list a podcast's episodes.
            if (HaMusicActions.IsPodcastEpisodes(svc))
            {
                var (code, output, error) = await HaPodcastEpisodes.RunAsync(musicClientFactory?.Invoke(), data, effectiveCt);
                return done(code, output, error);
            }

            // Served here too: the calendar's service catalog cannot list uids or delete at all.
            if (HaCalendarActions.IsCalendarAction(svc))
            {
                var (code, output, error) = await HaCalendarEvents.RunAsync(clientFactory(), svc, entityId, data, _time, effectiveCt);
                return done(code, output, error);
            }

            IReadOnlyDictionary<string, JsonNode?> payload = data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.DeepClone());
            var result = await clientFactory().CallServiceAsync(svc.Domain, svc.Service, entityId, payload, effectiveCt);
            var changed = new JsonArray(result.ChangedEntities
                .Select(e => (JsonNode?)$"{e.EntityId} → {e.State}").ToArray());
            var stdout = new JsonObject { ["ok"] = true, ["changed"] = changed };
            if (result.Response is not null)
            {
                stdout["response"] = result.Response.DeepClone();
            }
            return done(0, stdout.ToJsonString(), "");
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            // 124 is the GNU `timeout` convention; the prompt documents it alongside the other codes.
            return done(124, "", $"Action '{serviceName}.sh' timed out after {timeoutSeconds}s.", timedOut: true);
        }
        catch (HomeAssistantException ex)
        {
            // 400 = HA rejected the payload shape; 5xx = the payload was fine but the service
            // itself failed (e.g. play_media couldn't resolve a name in the MA library) — the
            // worst response there is nudging the caller back to --help to "fix" a shape that
            // was never wrong. 401/404 messages already say what's wrong; add nothing.
            var hint = ex.StatusCode switch
            {
                400 => $"\nRe-check the field types with `{serviceName}.sh --help`; don't retry the same shape.",
                >= 500 => "\nThe arguments were accepted but the action failed inside Home Assistant — a named item may not exist. For media, list the library (`browse_media.sh`) and use an exact title instead of retrying guesses. For a podcast episode no title resolves and `browse_media.sh` cannot expand a show: list them with `music_assistant.podcast_episodes.sh` and play the uri it returns.",
                _ => ""
            };
            return done(1, "", $"{ex.Message}{hint}");
        }
    }

    // Music Assistant's `play_index` reads `seek_position` as falsy-or-set: a 0 means "no seek
    // requested", so it substitutes the item's stored resume point and the stream restarts half a
    // second behind where the listener already was. Seeking a podcast episode or audiobook to the
    // start is therefore impossible through the honest value. 1 second is truthy for MA and
    // indistinguishable from 0 to a listener.
    private static void NormalizeMediaSeek(HaServiceDefinition svc, JsonObject data)
    {
        if (svc.Domain != "media_player" || svc.Service != "media_seek")
        {
            return;
        }

        // JsonNode.Parse, not JsonValue.Create: it yields the same JsonElement-backed value the arg
        // parser produces, which reads back as either int or double (see HaArgParser.Coerce).
        if (data["seek_position"] is JsonValue position && position.TryGetValue<double>(out var seconds) && seconds == 0)
        {
            data["seek_position"] = JsonNode.Parse("1");
        }
    }

    private static FsResult<FsExecResult> ExecResult(int exitCode, string stdout, string stderr, bool timedOut, long durationMs, string cwd) =>
        new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = exitCode,
            Truncated = false,
            TimedOut = timedOut,
            DurationMs = durationMs,
            Cwd = cwd
        });

    // Minimal shell tokeniser with bash's quoting rules: whitespace-split; single quotes keep
    // everything literal; double quotes honour `\"` and `\\` (and keep the backslash before any
    // other character); an unquoted backslash escapes the next character. A model writes a JSON
    // argument as `--description "{\"target\":…}"`, and without the escapes Home Assistant stored
    // `{\target\:…}` — an alarm whose target nothing could parse.
    private static List<string> ShellTokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var has = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'')
            {
                if (c == '\'')
                { quote = '\0'; }
                else
                { current.Append(c); }
            }
            else if (quote == '"')
            {
                if (c == '"')
                { quote = '\0'; }
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\')
                { current.Append(command[++i]); }
                else
                { current.Append(c); }
            }
            else if (c == '\\' && i + 1 < command.Length)
            {
                current.Append(command[++i]);
                has = true;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                has = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (has)
                { tokens.Add(current.ToString()); current.Clear(); has = false; }
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }
        if (has)
        { tokens.Add(current.ToString()); }
        return tokens;
    }
}