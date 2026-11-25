using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Tools;
using Mcp.Hosting;
using McpServerApps.Publishing;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace McpServerApps.Serving;

// Every request the proxy hands an app origin: which app is read from the Host (AppOrigin, by the
// id), who is asking from the app session the origin's own cookie names (AppViewers, which sends a page
// opened with none to the app sign-in and completes it on the way back). The published origin serves
// the served commit, to its author and, while the household shares it as an app space, to every member
// of its household; the preview the working tree, to its author alone (AccessAsync). Anyone else,
// and anything that is no app, gets one answer, byte for byte, so a stranger cannot tell an app that
// exists from one that does not.
public sealed class AppPages(
    BuiltAppStore store, AppViewers viewers, AgentReads reads, AppErrorReports reports, IHouseholdConfiguration households,
    AppsSettings settings, ILogger<AppPages> logger)
{
    private static readonly FileExtensionContentTypeProvider _types = new()
    {
        Mappings = { [".mjs"] = "text/javascript", [".webmanifest"] = "application/manifest+json", [".wasm"] = "application/wasm" }
    };

    public async Task ServeAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var request = context.Request;
        var origin = AppOrigin.Parse(request.Host.Value ?? "", settings.AppsDomain);
        var reading = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        if (origin is not null && reading && store.IsRemoved(origin.Id))
        {
            await RemovedAsync(context);
            return;
        }

        if (origin is not null && HttpMethods.IsPost(request.Method)
            && request.Path.StartsWithSegments(AppReads.OriginPath, out var operation)
            && !store.IsRemoved(origin.Id))
        {
            await ReadAsync(context, origin, operation.Value?.TrimStart('/') ?? "");
            return;
        }

        if (origin is not null && HttpMethods.IsPost(request.Method) && request.Path.Value == AppErrors.OriginPath
            && !store.IsRemoved(origin.Id))
        {
            await ReportAsync(context, origin);
            return;
        }

        if (origin is null || !reading)
        {
            await NotFoundAsync(context);
            return;
        }

        if (request.Path.Value == AppSignIn.CallbackPath)
        {
            await viewers.CompleteAsync(context, origin);
            return;
        }

        ServedFiles served;
        switch (await SettleAsync(request, origin, ct))
        {
            case Settled.ViewerUnchecked:
                await AppViewers.AnswerAsync(context, StatusCodes.Status503ServiceUnavailable, AppViewers.SignInUnchecked);
                return;
            // A page opened by nobody is sent to sign in whether or not its app exists, and learns nothing by it.
            case Settled.Nobody when IsNavigation(request) && await viewers.SendToSignInAsync(context):
                return;
            case Settled.AccessUnchecked:
                await AppViewers.AnswerAsync(context, StatusCodes.Status503ServiceUnavailable, AccessUnchecked);
                return;
            case Settled.Open open:
                served = open.Served;
                break;
            default:
                await NotFoundAsync(context);
                return;
        }

        var pinned = await served.PinnedAsync(ct);
        var policy = AppContentPolicy.For(served.Manifest, settings.Cdns, pinned);
        // The version a published origin serves, which its worker keeps the app's files under.
        var version = served is CommitFiles { Commit: var commit } ? AppOriginFiles.Version(commit, policy) : null;
        if (version is not null)
        {
            context.Response.Headers[AppOriginFiles.VersionHeader] = version;
        }

        if (request.Path.StartsWithSegments(AppOriginFiles.Prefix.TrimEnd('/')))
        {
            await GeneratedAsync(context, policy, served.Manifest, version);
            return;
        }

        if (Segments(request.Path.Value ?? "/") is not { } segments)
        {
            await AppAnswerAsync(context, policy, StatusCodes.Status404NotFound);
            return;
        }

        var relative = string.Join('/', segments);
        var entry = await served.EntryAsync(relative, ct);
        if (entry is CommittedEntry.Directory)
        {
            if (!(request.Path.Value ?? "/").EndsWith('/'))
            {
                context.Response.Headers.Location = $"{request.PathBase}{request.Path}/{request.QueryString}";
                await AppAnswerAsync(context, policy, StatusCodes.Status308PermanentRedirect);
                return;
            }

            relative = relative.Length == 0 ? "index.html" : $"{relative}/index.html";
            entry = await served.EntryAsync(relative, ct);
        }

        // A client-side route: a page being opened at an address the app has no file for is its own
        // index, which reads the address itself. Anything else missing is missing.
        if (entry is null && IsNavigation(request))
        {
            relative = "index.html";
            entry = await served.EntryAsync(relative, ct);
        }

        if (entry is not CommittedEntry.File { Bytes: var bytes })
        {
            await AppAnswerAsync(context, policy, StatusCodes.Status404NotFound);
            return;
        }

        if (!_types.TryGetContentType(relative, out var type))
        {
            type = "application/octet-stream";
        }

        if (type == "text/html")
        {
            var html = Encoding.UTF8.GetString(bytes);
            policy = AppContentPolicy.For(served.Manifest, settings.Cdns, pinned, InlineImportMaps.Hashes(html));
            bytes = Encoding.UTF8.GetBytes(AppOriginFiles.Inject(html));
        }

        await AppAnswerAsync(context, policy, StatusCodes.Status200OK, type, bytes);
    }

    // A page's read of the household (the client library's POST to /_vfs/<operation>): the viewer and the
    // app settled as for a page — the one 404 for anyone who may not open it — then handed to the agent
    // with the reads of the origin's own manifest (the served commit's, or the tree's on the preview) and
    // the assertion the app session holds, renewed just now if it was due. Never kept by anyone between.
    private async Task ReadAsync(HttpContext context, AppOrigin origin, string operation)
    {
        var ct = context.RequestAborted;
        if (!FromThisOrigin(context.Request))
        {
            await NotFoundAsync(context);
            return;
        }

        var settled = await SettleAsync(context.Request, origin, ct);
        if (settled is Settled.ViewerUnchecked or Settled.AccessUnchecked)
        {
            await ReadAnswerAsync(context, AppContentPolicy.Closed, AgentReadAnswer.Envelope(
                StatusCodes.Status503ServiceUnavailable, ToolError.TransientDependency(
                    settled is Settled.ViewerUnchecked
                        ? "Who is reading could not be checked just now, so nothing was read."
                        : "Whether this app is shared with the reader could not be checked just now, so nothing was read.",
                    "Read again in a moment.")));
            return;
        }

        if (settled is not Settled.Open { Session: var session, App: var app, Served: var served }
            || operation.Length == 0 || operation.Contains('/'))
        {
            await NotFoundAsync(context);
            return;
        }

        var policy = AppContentPolicy.For(served.Manifest, settings.Cdns);
        if (await ArgumentsAsync(context.Request, ct) is not { } arguments)
        {
            await ReadAnswerAsync(context, policy, AgentReadAnswer.Envelope(StatusCodes.Status400BadRequest,
                ToolError.Result(ToolError.Codes.InvalidArgument,
                    $"A read's body is one JSON object of the tool's arguments, at most {MaxArgumentBytes} bytes.")));
            return;
        }

        var scope = new AppReadScope { Id = app.Id, Agent = app.BuildingAgent, Reads = served.Manifest.Reads };
        await ReadAnswerAsync(context, policy, await reads.ReadAsync(operation, scope, arguments, session.Proof, ct));
    }

    // A runtime error a page of the app reports (the client library's POST to /_errors): the viewer and the
    // app settled as for a read — the one 404 for anyone who may not open it — then kept on that app alone,
    // stamped with who reported it, on which origin, from which browser and when (AppErrorReports).
    private async Task ReportAsync(HttpContext context, AppOrigin origin)
    {
        var ct = context.RequestAborted;
        if (!FromThisOrigin(context.Request))
        {
            await NotFoundAsync(context);
            return;
        }

        switch (await SettleAsync(context.Request, origin, ct))
        {
            case Settled.ViewerUnchecked:
                await AppViewers.AnswerAsync(context, StatusCodes.Status503ServiceUnavailable, AppViewers.SignInUnchecked);
                return;
            case Settled.AccessUnchecked:
                await AppViewers.AnswerAsync(context, StatusCodes.Status503ServiceUnavailable);
                return;
            case Settled.Open { Session: var session, App: var app, Served: var served }:
                await reports.ReportAsync(context, app, session, served, ct);
                return;
            default:
                await NotFoundAsync(context);
                return;
        }
    }

    // Who is asking and what of the origin they may have, settled the one way for a page, a read and a
    // report: the viewer before whether the app is there, so nobody learns whether an app exists by asking;
    // then whether this viewer may open it (AccessAsync), and what the origin serves them. Each caller says
    // how it answers each outcome.
    private async Task<Settled> SettleAsync(HttpRequest request, AppOrigin origin, CancellationToken ct)
    {
        var viewer = await viewers.ViewerAsync(request, origin, ct);
        if (viewer == AppViewer.Unchecked)
        {
            return Settled.ViewerUnchecked.Instance;
        }

        if (viewer is not AppViewer.Person { Session: var session })
        {
            return Settled.Nobody.Instance;
        }

        if (await store.FindAsync(origin.Id, ct) is not { } app)
        {
            return Settled.Closed.Instance;
        }

        switch (await AccessAsync(session, app, origin, ct))
        {
            case Access.Unchecked:
                return Settled.AccessUnchecked.Instance;
            case Access.Open { SharedAs: var sharedAs } when await ServedAsync(app, origin, sharedAs, ct) is { } served:
                return new Settled.Open(session, app, served);
            default:
                return Settled.Closed.Instance;
        }
    }

    private abstract record Settled
    {
        public sealed record Open(AppSession Session, BuiltApp App, ServedFiles Served) : Settled;

        // Who is asking could not be checked just now.
        public sealed record ViewerUnchecked : Settled
        {
            public static readonly ViewerUnchecked Instance = new();
        }

        // No app session on this origin.
        public sealed record Nobody : Settled
        {
            public static readonly Nobody Instance = new();
        }

        // No such app, one this viewer may not open, or nothing served there yet: the one 404.
        public sealed record Closed : Settled
        {
            public static readonly Closed Instance = new();
        }

        // Whether the app is shared with this viewer could not be read just now.
        public sealed record AccessUnchecked : Settled
        {
            public static readonly AccessUnchecked Instance = new();
        }
    }

    // Who may open an app's origins, its pages, reads and reports alike: its author; and the published
    // origin, while the household shares the app as an app space, every member of the app's household —
    // the household's spaces section's word, read on the viewer's own `apps` assertion, so an id another
    // household names opens nothing. A disabled member holds no app session for long: disabling ends their
    // sessions (EndedAppSessionsListener), and a renewal answers nobody. Whether the app is shared that
    // cannot be read just now is Unchecked for anyone but its author, never closed nor open: an outage is
    // no unsharing.
    //
    // Open names the space sharing the app, whose approved reach bounds what the published origin grants
    // every viewer, its author too. The author's published origin is asked the section as well, but never
    // closed by it: what cannot be read, or is refused, opens their own app under its whole manifest, which
    // reaches nobody's data but theirs.
    private async Task<Access> AccessAsync(AppSession session, BuiltApp app, AppOrigin origin, CancellationToken ct)
    {
        var author = session.Person == app.Author;
        if (author && origin.Preview)
        {
            return new Access.Open(null);
        }

        if (origin.Preview || session.Person.TenantId != app.HouseholdId
                           || session.Proof is not ViewerProof.Assertion { Token: var assertion })
        {
            return author ? new Access.Open(null) : Access.Closed.Instance;
        }

        switch (await AppSharing.OfAsync(CallHousehold.OfPerson(app.HouseholdId, assertion, households), app, ct))
        {
            case AppSharing.Read { SharedAs: var sharedAs }:
                return author || sharedAs is not null ? new Access.Open(sharedAs) : Access.Closed.Instance;
            case AppSharing.Unreadable { Cause: var cause }:
                logger.LogWarning(cause, "Could not read household {Household}'s spaces for app {App}", app.HouseholdId, app.Id);
                return author ? new Access.Open(null) : Access.Unchecked.Instance;
            case AppSharing.Refused { Cause: var cause }:
                logger.LogWarning(cause, "The accounts service refused household {Household}'s spaces for app {App}", app.HouseholdId, app.Id);
                return author ? new Access.Open(null) : Access.Closed.Instance;
            default:
                throw new InvalidOperationException("The spaces are read, unreadable or refused.");
        }
    }

    private abstract record Access
    {
        // SharedAs: the app space sharing the app, none when it is not shared.
        public sealed record Open(HouseholdSpace? SharedAs) : Access;

        public sealed record Closed : Access
        {
            public static readonly Closed Instance = new();
        }

        public sealed record Unchecked : Access
        {
            public static readonly Unchecked Instance = new();
        }
    }

    // Plain and self-contained, as the closed policy loads nothing.
    private const string AccessUnchecked =
        """
        <!doctype html>
        <html lang="en">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Could not open this app</title>
        <h1>Could not open this app right now</h1>
        <p>Whether it is shared with you could not be checked. Try again in a moment.</p>
        </html>
        """;

    // Every app shares the apps domain, so a sibling app's page is same-site: its POST here carries this
    // origin's cookie, and only its Origin header says it is not this origin's own page. A read or a report
    // is taken from this origin's pages alone.
    private static bool FromThisOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);

    // A read's arguments are a path, a pattern or a statement: small. A body larger is never held.
    private const int MaxArgumentBytes = 64 * 1024;

    private static async Task<JsonObject?> ArgumentsAsync(HttpRequest request, CancellationToken ct)
    {
        if (await BodyAsync(request, MaxArgumentBytes, ct) is not { } body)
        {
            return null;
        }

        try
        {
            return body.Length == 0 ? [] : JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A request's body, or null for one longer than max: a body larger is never held.
    internal static async Task<byte[]?> BodyAsync(HttpRequest request, int max, CancellationToken ct)
    {
        if (request.ContentLength > max)
        {
            return null;
        }

        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > max)
            {
                return null;
            }

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }

    // The agent's answer as it came, under the origin's policy, kept by nothing: a read is one viewer's.
    private static async Task ReadAnswerAsync(HttpContext context, string policy, AgentReadAnswer answer)
    {
        var response = context.Response;
        response.StatusCode = answer.Status;
        response.Headers.ContentSecurityPolicy = policy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.ContentType = answer.ContentType;
        response.ContentLength = answer.Body.Length;
        await response.Body.WriteAsync(answer.Body, context.RequestAborted);
    }

    // Asked of nobody: a removed app holds nothing to protect, and whoever installed it must hear.
    private static async Task RemovedAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path.Value == AppOriginFiles.WorkerPath)
        {
            context.Response.Headers["Service-Worker-Allowed"] = "/";
            await AppAnswerAsync(context, AppContentPolicy.Closed, StatusCodes.Status200OK, "text/javascript; charset=utf-8",
                Encoding.UTF8.GetBytes(AppOriginFiles.RemovedWorker));
            return;
        }

        if (!IsNavigation(request))
        {
            await NotFoundAsync(context);
            return;
        }

        await AppAnswerAsync(context, AppContentPolicy.Closed, StatusCodes.Status410Gone, "text/html; charset=utf-8",
            Encoding.UTF8.GetBytes(AppOriginFiles.RemovedPage));
    }

    // The server's own files under /_app/, which no file of the app's can replace.
    private static async Task GeneratedAsync(HttpContext context, string policy, AppManifest manifest, string? version)
    {
        var (type, body) = context.Request.Path.Value switch
        {
            AppOriginFiles.ManifestPath => ("application/manifest+json", AppOriginFiles.Manifest(manifest)),
            AppOriginFiles.IconPath => ("image/svg+xml", AppOriginFiles.Icon(manifest)),
            AppOriginFiles.WorkerPath => ("text/javascript", AppOriginFiles.Worker(version)),
            AppOriginFiles.RegisterPath => ("text/javascript", AppOriginFiles.Register),
            AppOriginFiles.LibraryPath => ("text/javascript", AppOriginFiles.Library),
            AppOriginFiles.ModulePath => ("text/javascript", AppOriginFiles.Module),
            _ => (null, null)
        };
        if (body is null)
        {
            await AppAnswerAsync(context, policy, StatusCodes.Status404NotFound);
            return;
        }

        if (context.Request.Path.Value == AppOriginFiles.WorkerPath)
        {
            // Served below the root, it may still control the whole origin.
            context.Response.Headers["Service-Worker-Allowed"] = "/";
        }

        await AppAnswerAsync(context, policy, StatusCodes.Status200OK, $"{type}; charset=utf-8", Encoding.UTF8.GetBytes(body));
    }

    // What an origin serves: the served commit's root for the published app (nothing until the first
    // publish), the working tree's for the preview, each with the manifest it was written with — the
    // published one's reach, while the app is shared, no more than an owner approved (sharedAs), so every
    // policy and read on that origin is granted only that.
    private async Task<ServedFiles?> ServedAsync(BuiltApp app, AppOrigin origin, HouseholdSpace? sharedAs, CancellationToken ct)
    {
        var repository = store.Repository(app);
        if (origin.Preview)
        {
            var manifestFile = Path.Combine(repository.WorkTree, AppManifest.FileName);
            return File.Exists(manifestFile) && AppManifest.Parse(await File.ReadAllTextAsync(manifestFile, ct)) is
                AppManifestParse.Valid { Manifest: var manifest }
                ? new WorkingTreeFiles(manifest, Path.Combine(repository.WorkTree, manifest.Root))
                : null;
        }

        return app.PublishedCommit is { } commit && await store.PublishedManifestAsync(app, ct) is { } published
            ? new CommitFiles(sharedAs is null ? published : Granted(published, sharedAs.Approved), repository, commit, app.PinnedAt(commit))
            : null;
    }

    private static AppManifest Granted(AppManifest manifest, AppReach? approved)
    {
        var granted = AppReach.Of(manifest).Within(approved);
        return manifest with { Reads = granted.Reads, Connect = granted.Connect };
    }

    // The path's segments, or null for one that tries to leave the root or reach git.
    private static IReadOnlyList<string>? Segments(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s is "." or ".." || s.Contains('\\') || s.Any(char.IsControl)
                                 || s.Equals(".git", StringComparison.OrdinalIgnoreCase))
            ? null
            : segments;
    }

    private static bool IsNavigation(HttpRequest request) =>
        request.Headers["Sec-Fetch-Mode"] == "navigate" || request.Headers.Accept.ToString().Contains("text/html");

    private static async Task AppAnswerAsync(
        HttpContext context, string policy, int status, string? type = null, byte[]? body = null)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.Headers.ContentSecurityPolicy = policy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        if (body is null)
        {
            return;
        }

        response.ContentType = type;
        response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    // The one answer for every request this server will not show: no app, nobody let in, nothing there.
    private static async Task NotFoundAsync(HttpContext context)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status404NotFound;
        response.Headers.ContentSecurityPolicy = AppContentPolicy.Closed;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.ContentType = "text/plain; charset=utf-8";
        await response.WriteAsync("Not found.", context.RequestAborted);
    }

    // What an origin serves at a page's path: one of the server's own files under /_app/, else the app's
    // (a directory too), else nothing.
    internal abstract record ServedFiles(AppManifest Manifest)
    {
        // A path below the manifest's root, its segments already checked.
        public abstract Task<CommittedEntry?> EntryAsync(string relative, CancellationToken ct);

        // The outside code its pages may load (AppContentPolicy): a commit's as its publish recorded it, the
        // working tree's as a publish would find it now, so the check sees what publishing would serve.
        public abstract Task<PinnedCode> PinnedAsync(CancellationToken ct);

        // The origin's own spelling of a path a page named, when it serves a file there (one of the server's
        // own under /_app/, else the app's) or, asked for, a directory: never the page's spelling, whose
        // slashes are the page's to choose.
        public async Task<string?> ServedPathAsync(string path, bool directories, CancellationToken ct)
        {
            if (Segments(path) is not { } segments)
            {
                return null;
            }

            var canonical = "/" + string.Join('/', segments);
            if (AppOriginFiles.Paths.Contains(canonical))
            {
                return canonical;
            }

            return canonical.StartsWith(AppOriginFiles.Prefix, StringComparison.Ordinal) ? null
                : await EntryAsync(string.Join('/', segments), ct) switch
                {
                    CommittedEntry.File => canonical,
                    CommittedEntry.Directory when directories => canonical,
                    _ => null
                };
        }
    }

    internal sealed record WorkingTreeFiles(AppManifest Manifest, string Root) : ServedFiles(Manifest)
    {
        public override async Task<CommittedEntry?> EntryAsync(string relative, CancellationToken ct)
        {
            var full = Path.GetFullPath(Path.Combine(Root, relative));
            if (!full.StartsWith(Path.GetFullPath(Root), StringComparison.Ordinal))
            {
                return null;
            }

            // A link would serve whatever it points at, inside the app or not: the tree serves files.
            if (File.Exists(full) && new FileInfo(full).LinkTarget is null)
            {
                return new CommittedEntry.File(await File.ReadAllBytesAsync(full, ct));
            }

            return Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null ? new CommittedEntry.Directory() : null;
        }

        public override async Task<PinnedCode> PinnedAsync(CancellationToken ct) =>
            Directory.Exists(Root) ? (await OutsideCodePinning.CheckRootAsync(Root, ct)).Pinned : PinnedCode.None;
    }

    internal sealed record CommitFiles(AppManifest Manifest, AppRepository Repository, string Commit, PinnedCode Pinned)
        : ServedFiles(Manifest)
    {
        public override Task<PinnedCode> PinnedAsync(CancellationToken ct) => Task.FromResult(Pinned);

        public override Task<CommittedEntry?> EntryAsync(string relative, CancellationToken ct) =>
            Repository.EntryAsync(Commit, string.Join('/', new[] { Manifest.Root, relative }.Where(p => p.Length > 0)), ct);
    }
}