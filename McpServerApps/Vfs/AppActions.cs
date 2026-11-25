using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using McpServerApps.Checks;
using McpServerApps.Publishing;
using McpServerApps.Store;

namespace McpServerApps.Vfs;

// An app's action files, the closed catalog the apps mount runs in an app's directory (as /timers' and
// /schedules' are): ./publish, ./history, ./rollback and ./check, each answered as a command's exit and
// output, never a shell. The mount resolves the app and the action; what each does is here.
public sealed class AppActions(BuiltAppStore store, AppChecks checks, string appsDomain)
{
    public const string Publish = "publish";
    public const string History = "history";
    public const string Rollback = "rollback";
    public const string Check = "check";

    public static readonly IReadOnlyList<string> Actions = [Publish, History, Rollback, Check];

    // The app's own assertions (`checks/*.js` in its working tree), each run inside the page.
    public const string AssertionsDirectory = "checks";

    // Viewports a check may ask for, and how large: a screenshot is one image the model is shown.
    private const int MaxViewports = 4;
    private const int MinViewportSide = 240;
    private const int MaxViewportSide = 3840;

    public static string Describe =>
        $"Run an app's action in its directory: ./{Publish} commits the working tree and serves it, answering "
        + "the app's link (refused while a served page loads outside code without an exact version and integrity hash); "
        + $"./{History} [--limit N] lists its commits, the served one marked; ./{Rollback} <rev> "
        + $"serves an earlier commit without rewriting history; ./{Check} [--viewport WxH]... [--path /route] opens "
        + "the preview in a headless browser as the person (a phone and a desktop unless named), runs each "
        + $"{AssertionsDirectory}/*.js in the page (an async function body: returning passes, throwing fails) and "
        + $"writes one PNG per viewport and report.json (console, failed requests, policy violations, an "
        + $"accessibility snapshot, assertions) to {AppsFileSystem.ChecksOutput}/latest/. Not a shell — anything else returns exit 127.";

    // Who runs an action, as the mount's call carries it: the turn's agent (named in a publish's commit),
    // the call's household (asked what of a publish waits on an owner) and the call's own token (what a
    // check reads as).
    public sealed record Caller(string? AgentId, ICallHousehold? Household, string? CallToken);

    // `action` as the command named it, without its `./`; `cwd` the directory exec ran in, answered back.
    public async Task<FsResult<FsExecResult>> RunAsync(
        BuiltApp app, string cwd, string action, IReadOnlyList<string> args, Caller caller, CancellationToken ct) =>
        action switch
        {
            Publish when args.Count == 0 => await PublishAsync(app, cwd, caller, ct),
            Publish => Exec(cwd, "", $"{Publish} takes no arguments: it commits the whole working tree and serves it.", 2),
            History => await HistoryAsync(app, cwd, args, ct),
            Rollback when args is [var revision] => await RollbackAsync(app, cwd, revision, ct),
            Rollback => Exec(cwd, "", $"usage: ./{Rollback} <rev> — a commit from ./{History}.", 2),
            Check => await CheckAsync(app, cwd, args, caller, ct),
            _ => Exec(cwd, "", $"command not found: {action}\navailable: {string.Join(", ", Actions)}", 127)
        };

    // The publish itself runs one at a time per household; what of it waits on an owner is asked after, of
    // the household's spaces, so no other publish waits on the accounts service.
    private async Task<FsResult<FsExecResult>> PublishAsync(BuiltApp app, string cwd, Caller caller, CancellationToken ct)
    {
        var (result, served) = await store.OneAtATimeAsync(app.HouseholdId, async () =>
        {
            var repository = store.Repository(app);
            var manifestPath = Path.Combine(repository.WorkTree, AppManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                return (Exec(cwd, "", $"nothing published: {AppManifest.FileName} is missing.", 1), null);
            }

            AppManifest manifest;
            switch (AppManifest.Parse(await File.ReadAllTextAsync(manifestPath, ct)))
            {
                case AppManifestParse.Refused { Problems: var problems }:
                    return (Exec(cwd, "", $"nothing published: {AppManifest.FileName} breaks its rules:\n- {string.Join("\n- ", problems)}", 1), null);
                case AppManifestParse.Valid { Manifest: var valid } when !Directory.Exists(Path.Combine(repository.WorkTree, valid.Root)):
                    return (Exec(cwd, "", $"nothing published: the root '{valid.Root}' the manifest serves is not a directory of the app.", 1), null);
                case AppManifestParse.Valid { Manifest: var valid }:
                    manifest = valid;
                    break;
                default:
                    throw new InvalidOperationException("A manifest is either valid or refused.");
            }

            if (IconProblem(app, Path.Combine(repository.WorkTree, manifest.Root), manifest) is { } icon)
            {
                return (Exec(cwd, "", $"nothing published: {icon}", 1), null);
            }

            var pinning = await OutsideCodePinning.CheckRootAsync(Path.Combine(repository.WorkTree, manifest.Root), ct);
            if (pinning.Problems.Count > 0)
            {
                return (Exec(cwd, "", $"nothing published: outside code must be pinned by an exact version and its integrity hash:\n- {string.Join("\n- ", pinning.Problems)}", 1), null);
            }

            var committed = await repository.CommitAllAsync($"Publish from a turn of {caller.AgentId ?? app.BuildingAgent}", ct);
            var head = (await repository.HeadAsync(ct))!;
            var commit = await repository.ResolveAsync(head, ct);
            // What passed is what the served policy loads, recorded with the commit so serving parses nothing.
            await store.SaveAsync(app with
            {
                PublishedCommit = head,
                Pinned = new Dictionary<string, PinnedCode>(app.Pinned) { [head] = pinning.Pinned }
            }, ct);
            var link = BuiltAppId.Link(app.Slug, app.Id, appsDomain);
            return (Exec(cwd, committed is null
                ? $"nothing changed since {commit!.Short}; serving it at {link}\n"
                : $"published {commit!.Short}; the app is served at {link}\n", "", 0), (AppManifest?)manifest);
        }, ct);

        return served is not null && result is FsResult<FsExecResult>.Ok { Value: var answer }
            ? new FsResult<FsExecResult>.Ok(answer with { Stdout = answer.Stdout + await WaitingOnAnOwnerAsync(app, served, caller.Household, ct) })
            : result;
    }

    // The icon the manifest names is what the published origin will install with: a file under the served
    // root (never a link, which would serve whatever it points at), and an SVG drawing.
    private static string? IconProblem(BuiltApp app, string root, AppManifest manifest)
    {
        if (manifest.Icon is not { } icon)
        {
            return null;
        }

        var file = Path.Combine(root, AppIcon.Relative(icon));
        if (!File.Exists(file) || new FileInfo(file).LinkTarget is not null)
        {
            return $"the icon {icon} is not a file the app serves: put the SVG at /{string.Join('/', new[] { app.Slug, manifest.Root, AppIcon.Relative(icon) }.Where(part => part.Length > 0))} "
                   + "(under the served root), or name the one it has.";
        }

        return AppIcon.IsDrawing(File.ReadAllBytes(file))
            ? null
            : $"the icon {icon} is not an SVG drawing: a whole <svg> document of the SVG namespace, at most {AppIcon.MaxBytes / 1024} KiB.";
    }

    // While the household shares the app as a space, its published origin is granted no more than an owner
    // approved: what the served manifest asks beyond that is named, for the agent to tell its person, since
    // nothing else tells the author it is not granted. Nothing is said of an app nobody shares, or one asking
    // no more; spaces that cannot be read say the check could not be made, and the publish stands.
    private async Task<string> WaitingOnAnOwnerAsync(BuiltApp app, AppManifest served, ICallHousehold? household, CancellationToken ct)
    {
        if (household is null)
        {
            return "";
        }

        var sharing = await AppSharing.OfAsync(household, app, ct);
        if (sharing is not AppSharing.Read { SharedAs: var space })
        {
            return "whether the household's owners approved what it reads and talks to could not be checked just now: while the "
                   + "household shares the app as a space, it is granted no more than they approved\n";
        }

        if (space is null || AppReach.Of(served).Beyond(space.Approved) is not { IsEmpty: false } beyond)
        {
            return "";
        }

        var asked = beyond.Reads.Select(read => $"reading {read}")
            .Concat(beyond.Connect.Select(origin => $"talking to {origin}"));
        return $"the household shares it as the space '{space.Name}', and an owner has not approved all it now asks: until one "
               + $"approves it on the spaces page, it is not granted {string.Join(", ", asked)} — for anyone, you included. "
               + "Tell the person, so they can ask an owner\n";
    }

    // The preview checked in the server's browser as the person whose turn this is, on this call's own
    // token, which the agent believes for that person's reads while the check runs.
    private async Task<FsResult<FsExecResult>> CheckAsync(BuiltApp app, string cwd, IReadOnlyList<string> args, Caller caller, CancellationToken ct)
    {
        if (CheckArguments(args) is not var (viewports, route))
        {
            return Exec(cwd, "",
                $"usage: ./{Check} [--viewport WxH]... [--path /route] — at most {MaxViewports} viewports, each side "
                + $"{MinViewportSide} to {MaxViewportSide}; the route starts with /.", 2);
        }

        if (caller.CallToken is not { } callToken)
        {
            return FsError.Fail<FsExecResult>(ToolError.Codes.PermissionDenied,
                "This call carries no token the agent signed, so nobody's reads could be shown in a check.");
        }

        var tree = store.Repository(app).WorkTree;
        var manifestPath = Path.Combine(tree, AppManifest.FileName);
        if (!File.Exists(manifestPath)
            || AppManifest.Parse(await File.ReadAllTextAsync(manifestPath, ct)) is not AppManifestParse.Valid { Manifest: var manifest })
        {
            return Exec(cwd, "", $"nothing checked: {AppManifest.FileName} is missing or breaks its rules, so the preview serves nothing.", 1);
        }

        var assertionsDirectory = Path.Combine(tree, AssertionsDirectory);
        var assertions = Directory.Exists(assertionsDirectory)
            ? await Task.WhenAll(Directory.EnumerateFiles(assertionsDirectory, "*.js")
                .Order(StringComparer.Ordinal)
                .Select(async file => new CheckAssertion($"{AssertionsDirectory}/{Path.GetFileName(file)}", await File.ReadAllTextAsync(file, ct))))
            : [];
        var outcome = await checks.RunAsync(app, new CheckRequest(viewports, route, manifest, assertions, callToken), ct);
        return outcome switch
        {
            CheckOutcome.Refused { Limit: var limit, Value: var value } => AppsFileSystem.Quota<FsExecResult>(limit, value,
                "Another check is running; tell the person, and check again once it has finished."),
            CheckOutcome.Ran { Report: var report } => Exec(cwd, CheckSummary(app, report),
                report.Error ?? "", report.DeadlineHit || report.Error is not null || report.Viewports.Any(v => v.Assertions.Any(a => !a.Passed)) ? 1 : 0),
            _ => throw new InvalidOperationException("A check runs or is refused.")
        };
    }

    // `--viewport WxH` (repeatable) and `--path /route`, or null for anything else.
    private static (IReadOnlyList<CheckViewport> Viewports, string Path)? CheckArguments(IReadOnlyList<string> args)
    {
        var viewports = new List<CheckViewport>();
        var route = "/";
        for (var i = 0; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count)
            {
                return null;
            }

            switch (args[i], args[i + 1])
            {
                case ("--viewport", var size) when size.Split('x') is [var w, var h]
                                                  && int.TryParse(w, out var width) && int.TryParse(h, out var height)
                                                  && width is >= MinViewportSide and <= MaxViewportSide
                                                  && height is >= MinViewportSide and <= MaxViewportSide:
                    viewports.Add(new CheckViewport(width, height));
                    break;
                case ("--path", var asked) when asked.StartsWith('/') && asked.Length <= 512 && !asked.Any(char.IsWhiteSpace):
                    route = asked;
                    break;
                default:
                    return null;
            }
        }

        return viewports.Count > MaxViewports ? null : (viewports.Count == 0 ? AppChecks.DefaultViewports : viewports.Distinct().ToList(), route);
    }

    // Counts per viewport, then what went wrong, so a fix needs no read of the report: a viewport that saw
    // exactly what one before it saw names that one instead of saying it all again.
    private static string CheckSummary(BuiltApp app, CheckReport report)
    {
        var detailed = new List<(string Viewport, IReadOnlyList<string> Lines)>();
        var lines = report.Viewports.SelectMany(v =>
        {
            var counts = $"- {v.Name}: {v.Console.Count(c => c.Type is "error" or "uncaught")} console errors, {v.FailedRequests.Count} failed requests, "
                         + $"{v.PolicyViolations.Count} policy violations, {v.RefusedReads.Count} refused reads, assertions "
                         + $"{v.Assertions.Count(a => a.Passed)} passed and {v.Assertions.Count(a => !a.Passed)} failed"
                         + (v.Settled ? "" : " (the page never went quiet)");
            var details = CheckDetails(v, new Uri(report.Origin).Host);
            var same = details.Count == 0 ? null : detailed.FirstOrDefault(d => d.Lines.SequenceEqual(details)).Viewport;
            detailed.Add((v.Name, details));
            return same is not null ? [counts, $"  the same as {same}"] : (IEnumerable<string>)[counts, .. details];
        }).ToList();
        var stopped = report.DeadlineHit ? $"stopped at its deadline of {report.Deadline.TotalSeconds:0} s; " : "";
        return $"checked the preview of {app.Slug} at {report.Path}: {stopped}\n{string.Join("\n", lines)}\n"
               + $"screenshots and {AppChecks.ReportFile} are in /{app.Slug}/{AppsFileSystem.ChecksOutput}/{AppChecks.LatestDirectory}/ "
               + "(file_read shows a screenshot)\n";
    }

    private const int MaxDetailLines = 12;
    private const int MaxDetailLength = 240;

    private static IReadOnlyList<string> CheckDetails(ViewportReport v, string preview)
    {
        var all = v.Assertions.Where(a => !a.Passed).Select(a => $"failed: {a.File}: {a.Message}")
            .Concat(v.Console.Where(c => c.Type is "error" or "uncaught")
                .Select(c => c.Type == "uncaught" ? $"uncaught: {c.Text}" : $"console error{Where(c.Location, preview)}: {c.Text}"))
            .Concat(v.FailedRequests.Select(r => $"failed request: {r.Method} {r.Url}: {(r.Status is { } status ? status : r.Error)}"))
            .Concat(v.PolicyViolations.Select(p => $"policy violation: {p.Directive} {p.BlockedUri ?? p.SourceFile ?? "inline"}"))
            .Concat(v.RefusedReads.Select(r => $"refused read: {r.Operation} {r.Path}: {r.ErrorCode ?? "unknown"}"
                                               + (r.Message is { } why ? $": {why}" : "")))
            .Select(line => $"  {Clip(line)}")
            .ToList();
        return all.Count <= MaxDetailLines
            ? all
            : [.. all.Take(MaxDetailLines), $"  and {all.Count - MaxDetailLines} more in {AppChecks.ReportFile}"];
    }

    // Where a console line came from, before its text so clipping keeps it: Chromium's line for a failed
    // load names the address only here. The preview's own addresses by path, a load's ":0:0" dropped.
    private static string Where(string? location, string preview)
    {
        if (string.IsNullOrEmpty(location))
        {
            return "";
        }

        var at = location.EndsWith(":0:0", StringComparison.Ordinal) ? location[..^4] : location;
        return Uri.TryCreate(at, UriKind.Absolute, out var uri) && string.Equals(uri.Host, preview, StringComparison.OrdinalIgnoreCase)
            ? $" at {uri.PathAndQuery}"
            : $" at {at}";
    }

    // One line of a detail: its first line only (an uncaught error's stack stays in the report), clipped.
    private static string Clip(string line)
    {
        var first = line.Split('\n')[0].TrimEnd();
        return first.Length <= MaxDetailLength ? first : $"{first[..MaxDetailLength]}…";
    }

    private async Task<FsResult<FsExecResult>> HistoryAsync(BuiltApp app, string cwd, IReadOnlyList<string> args, CancellationToken ct)
    {
        var limit = 20;
        if (args is not [] && !(args is ["--limit", var n] && int.TryParse(n, out limit) && limit is > 0 and <= 1000))
        {
            return Exec(cwd, "", $"usage: ./{History} [--limit N] — N from 1 to 1000.", 2);
        }

        var commits = await store.Repository(app).LogAsync(limit, ct);
        if (commits.Count == 0)
        {
            return Exec(cwd, $"no commits yet: ./{Publish} makes the first\n", "", 0);
        }

        var lines = commits.Select(c =>
            $"{(c.Sha == app.PublishedCommit ? "*" : " ")} {c.Short}  {c.At.UtcDateTime:yyyy-MM-dd HH:mm}Z  {c.Subject}"
            + (c.Sha == app.PublishedCommit ? "  (served)" : ""));
        return Exec(cwd, string.Join("\n", lines) + "\n", "", 0);
    }

    private async Task<FsResult<FsExecResult>> RollbackAsync(BuiltApp app, string cwd, string revision, CancellationToken ct) =>
        await store.OneAtATimeAsync(app.HouseholdId, async () =>
        {
            if (await store.Repository(app).ResolveAsync(revision, ct) is not { } commit)
            {
                return Exec(cwd, "", $"no commit '{revision}' in this app: ./{History} lists them.", 1);
            }

            await store.SaveAsync(app with { PublishedCommit = commit.Sha }, ct);
            return Exec(cwd,
                $"serving {commit.Short} ({commit.Subject}) at {BuiltAppId.Link(app.Slug, app.Id, appsDomain)}; "
                + $"history is unchanged, and ./{Publish} serves the working tree again\n", "", 0);
        }, ct);

    private static FsResult<FsExecResult> Exec(string cwd, string stdout, string stderr, int exitCode) =>
        new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = stdout, Stderr = stderr, ExitCode = exitCode, Truncated = false, TimedOut = false, DurationMs = 0, Cwd = cwd
        });
}