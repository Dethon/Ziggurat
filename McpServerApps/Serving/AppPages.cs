using System.Text;
using Domain.Apps;
using Domain.DTOs.Channel;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace McpServerApps.Serving;

// Every request the proxy hands an app origin: which app is read from the Host (AppOrigin, by the
// id), who is asking from the app session the origin's own cookie names. The published origin serves
// the served commit, to its author; the preview the working tree, to its author alone. Anyone else,
// and anything that is no app, gets one answer, byte for byte, so a stranger cannot tell an app that
// exists from one that does not.
public sealed class AppPages(BuiltAppStore store, AppSessions sessions, AppsSettings settings)
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

        if (origin is null
            || !reading
            || await store.FindAsync(origin.Id, ct) is not { } app
            || Viewer(request, origin) is not { } viewer
            || viewer != app.Author
            || await ServedAsync(app, origin, ct) is not { } served)
        {
            await NotFoundAsync(context);
            return;
        }

        var policy = AppContentPolicy.For(served.Manifest, settings.Cdns);
        if (request.Path.StartsWithSegments(AppOriginFiles.Prefix.TrimEnd('/')))
        {
            await GeneratedAsync(context, policy, served.Manifest);
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
            bytes = Encoding.UTF8.GetBytes(AppOriginFiles.Inject(Encoding.UTF8.GetString(bytes)));
        }

        await AppAnswerAsync(context, policy, StatusCodes.Status200OK, type, bytes);
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
    private static async Task GeneratedAsync(HttpContext context, string policy, AppManifest manifest)
    {
        var (type, body) = context.Request.Path.Value switch
        {
            AppOriginFiles.ManifestPath => ("application/manifest+json", AppOriginFiles.Manifest(manifest)),
            AppOriginFiles.IconPath => ("image/svg+xml", AppOriginFiles.Icon(manifest)),
            AppOriginFiles.WorkerPath => ("text/javascript", AppOriginFiles.Worker),
            AppOriginFiles.RegisterPath => ("text/javascript", AppOriginFiles.Register),
            AppOriginFiles.LibraryPath => ("text/javascript", AppOriginFiles.Library),
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

    private Account? Viewer(HttpRequest request, AppOrigin origin) =>
        request.Cookies[AppSessions.CookieName] is { Length: > 0 } token ? sessions.Find(token, origin)?.Person : null;

    // What an origin serves: the served commit's root for the published app (nothing until the first
    // publish), the working tree's for the preview, each with the manifest it was written with.
    private async Task<ServedFiles?> ServedAsync(BuiltApp app, AppOrigin origin, CancellationToken ct)
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

        if (app.PublishedCommit is not { } commit
            || await repository.EntryAsync(commit, AppManifest.FileName, ct) is not CommittedEntry.File { Bytes: var json }
            || AppManifest.Parse(Encoding.UTF8.GetString(json)) is not AppManifestParse.Valid { Manifest: var published })
        {
            return null;
        }

        return new CommitFiles(published, repository, commit);
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

    private abstract record ServedFiles(AppManifest Manifest)
    {
        // A path below the manifest's root, its segments already checked.
        public abstract Task<CommittedEntry?> EntryAsync(string relative, CancellationToken ct);
    }

    private sealed record WorkingTreeFiles(AppManifest Manifest, string Root) : ServedFiles(Manifest)
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
    }

    private sealed record CommitFiles(AppManifest Manifest, AppRepository Repository, string Commit) : ServedFiles(Manifest)
    {
        public override Task<CommittedEntry?> EntryAsync(string relative, CancellationToken ct) =>
            Repository.EntryAsync(Commit, string.Join('/', new[] { Manifest.Root, relative }.Where(p => p.Length > 0)), ct);
    }
}