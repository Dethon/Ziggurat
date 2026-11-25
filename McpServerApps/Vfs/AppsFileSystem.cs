using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Files;
using Domain.Tools.FileSystem;
using Domain.Tools.Text;
using McpServerApps.Checks;
using McpServerApps.Store;

namespace McpServerApps.Vfs;

// The calling person's built apps as a mount: /apps/<slug>/ is one app's working tree, written with
// the file tools the agent already has. Writing /apps/<slug>/app.json creates the app; its action
// files publish it, list its history, roll it back and check it, on the mount itself so they need no
// sandbox; what a check saw is read back from `/<slug>/.checks/latest/`, and the runtime errors its pages
// reported from `/<slug>/errors`, which only the server writes;
// removing the directory deletes it. Each call acts for the person its verified token names, in that
// person's household: another member's apps are neither listed nor reachable, and a call naming
// nobody is refused rather than shown an empty list.
public sealed class AppsFileSystem(BuiltAppStore store, AppChecks checks, string appsDomain) : FileSystemBackendBase
{
    public const string Name = "apps";

    // Where the mount shows what the app's last check saw (BuiltAppStore.ChecksDirectory), read-only.
    public const string ChecksOutput = ".checks";

    // Where the mount shows the runtime errors the app's pages reported (BuiltAppStore.ErrorsFile), read-only.
    public const string ErrorsOutput = "errors";

    // The longest slug whose preview label (`<slug>-<id>-preview`) is still one DNS label.
    public const int SlugLength = 40;

    // Names at an app's root the server keeps for what it produces itself (checks, runtime errors);
    // never the agent's to write, so a later release can serve them without colliding with a file.
    private static readonly string[] _reserved = [.. AppActions.Actions, ErrorsOutput, ChecksOutput];

    // What the text read opens: the shared text list plus what a web app is made of besides.
    private static readonly string[] _textFiles =
        [.. TextFileExtensions.Default, ".svg", ".webmanifest", ".map", ".vue", ".svelte", ".astro", ".jsonc", ".mdx"];

    private Account? _author;
    private string? _agentId;
    private ICallHousehold? _household;
    private string? _callToken;

    private readonly AppActions _actions = new(store, checks, appsDomain);

    public override FileSystemBackendBase For(FileSystemCaller caller)
    {
        var view = (AppsFileSystem)MemberwiseClone();
        view._author = Principal.Of(caller.Conversation)?.Account;
        view._agentId = caller.Conversation?.AgentId;
        view._household = caller.Household;
        view._callToken = caller.CallToken;
        return view;
    }

    public override string FilesystemName => Name;

    public override string DescribeMount =>
        "The person's own built apps — small read-only web apps, each a git repository served on an origin "
        + "of its own: /apps/<slug>/ is one app's working tree. Writing /apps/<slug>/app.json creates an "
        + "app; the action files ./publish, ./history [--limit N] and ./rollback <rev> in its directory "
        + "publish the working tree, list its commits and serve an earlier one; ./check opens the preview in a "
        + "browser as the person and leaves screenshots and report.json in /<slug>/.checks/latest/; "
        + "/<slug>/errors, once a page has reported one, lists the runtime errors people's browsers met (uncaught "
        + "errors, unhandled rejections, refused reads), one JSON object a line, newest first; removing "
        + "the directory deletes the app. Writes under node_modules/ are refused.";

    public override string DescribeCreate =>
        "Write a text file into an app's working tree. /<slug>/app.json creates the app (its manifest: "
        + "name, shortName?, accentColor, description, root?, reads?, connect?), checked whole before it is kept.";

    public override string DescribeEdit =>
        "Edit a text file of an app's working tree by an ordered list of replacements; app.json is checked "
        + "whole before the edit is kept.";

    public override string DescribeDelete =>
        "Delete a file or directory of an app's working tree; deleting /<slug> deletes the whole app, refused while "
        + "the household shares it as a space.";

    public override string DescribeMove =>
        "Move or rename within one app's working tree; moving /<slug> to /<new-slug> renames the app, "
        + "and its old links keep working.";

    public override string DescribeCopy =>
        "Copy a file or directory into one of the person's apps, from the same app or another of theirs.";

    public override string DescribeExec => AppActions.Describe;

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        if (_author is not { } author)
        {
            return Nobody<FsGlobResult>();
        }

        var apps = await store.ListAsync(author.TenantId, author, ct);
        var candidates = apps.SelectMany(app => Listing(app).Select(c => (c.Path, c.IsDirectory))).ToList();
        var actions = apps.SelectMany(app => AppActions.Actions.Select(action => $"/{app.Slug}/{action}")).ToHashSet(StringComparer.Ordinal);
        return Glob(
            pattern,
            () =>
            [
                .. candidates
                    .Where(c => (!scope.DirsOnly || c.IsDirectory) && scope.Matches(c.Path))
                    .Select(c => c.IsDirectory ? $"/{c.Path}/" : $"/{c.Path}")
                    .OrderBy(p => p, StringComparer.Ordinal)
            ],
            actions.Contains);
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsInfoResult>();
        }

        return await ResolveAsync(path, ct) switch
        {
            AppNode.Root or AppNode.AppDirectory => Info(path, true, true),
            AppNode.Action => new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = false, Executable = true }),
            AppNode.Entry { Full: var full } when File.Exists(full) => new FsResult<FsInfoResult>.Ok(new FsInfoResult
            {
                Exists = true,
                Path = path,
                IsDirectory = false,
                Size = new FileInfo(full).Length,
                LastModified = File.GetLastWriteTimeUtc(full).ToString("O")
            }),
            AppNode.Entry { Full: var full } when Directory.Exists(full) => new FsResult<FsInfoResult>.Ok(new FsInfoResult
            {
                Exists = true, Path = path, IsDirectory = true, LastModified = Directory.GetLastWriteTimeUtc(full).ToString("O")
            }),
            AppNode.Invalid { Reason: var reason } => Invalid<FsInfoResult>(reason),
            _ => Info(path, false, null)
        };
    }

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsReadResult>();
        }

        return await ResolveAsync(path, ct) switch
        {
            AppNode.Action { Name: var action } => ExecutableOnly<FsReadResult>(path, action),
            AppNode.Entry { Full: var full, Base: var root } when File.Exists(full) =>
                new TextReadTool(root, _textFiles).Run(full, offset, limit)
                    .Map(read => read with { FilePath = path }),
            AppNode.Root or AppNode.AppDirectory => Invalid<FsReadResult>($"'{path}' is a directory: glob it to list it."),
            AppNode.Entry { Full: var full } when Directory.Exists(full) => Invalid<FsReadResult>($"'{path}' is a directory: glob it to list it."),
            AppNode.Invalid { Reason: var reason } => Invalid<FsReadResult>(reason),
            _ => NotFound<FsReadResult>(path)
        };
    }

    public override async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        if (_author is not { } author)
        {
            return Nobody<FsSearchResult>();
        }

        if (!CompileFilePattern(filePattern).TryGetValue(out var admits, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        var scope = (path ?? directoryPath ?? "").Trim('/');
        var files = (await store.ListAsync(author.TenantId, author, ct))
            .SelectMany(app => Listing(app).Where(c => !c.IsDirectory).Select(c => (App: app, c.Path, c.Full)))
            .Where(f => scope.Length == 0 || f.Path == scope || f.Path.StartsWith(scope + "/", StringComparison.Ordinal))
            .Where(f => !AppActions.Actions.Contains(Path.GetFileName(f.Path)) || f.Path.Count(c => c == '/') > 1)
            .Where(f => _textFiles.Contains(Path.GetExtension(f.Path).ToLowerInvariant()) && admits(Path.GetFileName(f.Path)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        return await SearchNodesAsync(
            files,
            async (file, token) => ($"/{file.Path}", await File.ReadAllTextAsync(file.Full, token)),
            new FsSearchScan
            {
                Query = query,
                Regex = regex,
                Path = "/" + scope,
                MaxResults = maxResults,
                ContextLines = contextLines,
                OutputMode = outputMode
            },
            ct);
    }

    public override async Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (_author is not { } author)
        {
            return Nobody<FsCreateResult>();
        }

        switch (await ResolveAsync(path, ct))
        {
            case AppNode.Missing { Slug: var slug, Relative: AppManifest.FileName }:
                return await CreateAppAsync(author, slug, path, content, ct);
            case AppNode.Missing { Slug: var slug }:
                return NoSuchApp<FsCreateResult>(slug);
            case AppNode.Root or AppNode.AppDirectory:
                return Invalid<FsCreateResult>($"Write a file, not a directory: an app is created by writing /<slug>/{AppManifest.FileName}.");
            case AppNode.Action { Name: var action }:
                return ExecutableOnly<FsCreateResult>(path, action);
            case AppNode.Invalid { Reason: var reason }:
                return Invalid<FsCreateResult>(reason);
            case AppNode.Entry entry:
                if (WriteRefusal<FsCreateResult>(entry) is { } refused)
                {
                    return refused;
                }

                if (File.Exists(entry.Full) && !overwrite)
                {
                    return FsError.AlreadyExists<FsCreateResult>($"'{path}' already exists: set overwrite to replace it.");
                }

                if (Directory.Exists(entry.Full))
                {
                    return Invalid<FsCreateResult>($"'{path}' is a directory.");
                }

                if (entry.Relative == AppManifest.FileName && await ManifestRefusalAsync<FsCreateResult>(content, ct) is { } manifest)
                {
                    return manifest;
                }

                var parent = Path.GetDirectoryName(entry.Full)!;
                if (!Directory.Exists(parent) && !createDirectories)
                {
                    return NotFound<FsCreateResult>(Path.GetDirectoryName(path)!.Replace('\\', '/'));
                }

                var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
                if (OverQuota<FsCreateResult>(entry.App, bytes, ExistingLength(entry.Full)) is { } quota)
                {
                    return quota;
                }

                Directory.CreateDirectory(parent);
                var existed = File.Exists(entry.Full);
                await File.WriteAllTextAsync(entry.Full, content, ct);
                return new FsResult<FsCreateResult>.Ok(new FsCreateResult
                {
                    Status = existed ? "overwritten" : "created",
                    FilePath = path,
                    Size = bytes.ToString(),
                    Lines = content.Split('\n').Length
                });
            default:
                return NotFound<FsCreateResult>(path);
        }
    }

    public override async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsEditResult>();
        }

        switch (await ResolveAsync(path, ct))
        {
            case AppNode.Action { Name: var action }:
                return ExecutableOnly<FsEditResult>(path, action);
            case AppNode.Invalid { Reason: var reason }:
                return Invalid<FsEditResult>(reason);
            case AppNode.Entry entry when File.Exists(entry.Full):
                if (WriteRefusal<FsEditResult>(entry) is { } refused)
                {
                    return refused;
                }

                if (edits.Count == 0)
                {
                    return Invalid<FsEditResult>("edits must contain at least one entry.");
                }

                if (!TextEditTool.Apply(await File.ReadAllTextAsync(entry.Full, ct), edits).TryGetValue(out var applied, out var error))
                {
                    return new FsResult<FsEditResult>.Err(error);
                }

                if (entry.Relative == AppManifest.FileName && await ManifestRefusalAsync<FsEditResult>(applied.Content, ct) is { } manifest)
                {
                    return manifest;
                }

                if (OverQuota<FsEditResult>(entry.App, System.Text.Encoding.UTF8.GetByteCount(applied.Content), ExistingLength(entry.Full)) is { } quota)
                {
                    return quota;
                }

                await File.WriteAllTextAsync(entry.Full, applied.Content, ct);
                return new FsResult<FsEditResult>.Ok(new FsEditResult
                {
                    Status = "success",
                    FilePath = path,
                    TotalOccurrencesReplaced = applied.TotalReplaced,
                    Edits = applied.Edits
                });
            default:
                return NotFound<FsEditResult>(path);
        }
    }

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsRemoveResult>();
        }

        switch (await ResolveAsync(path, ct))
        {
            case AppNode.AppDirectory { App: var app }:
                if (await SharedRefusalAsync<FsRemoveResult>(app, ct) is { } shared)
                {
                    return shared;
                }

                await store.OneAtATimeAsync(app.HouseholdId, async () =>
                {
                    await store.DeleteAsync(app);
                    return true;
                }, ct);
                return Removed(path, $"deleted the app {app.Slug}: its repository and its link are gone");
            case AppNode.Action { Name: var action }:
                return ExecutableOnly<FsRemoveResult>(path, action);
            case AppNode.Root:
                return Invalid<FsRemoveResult>("Remove one app's directory, /<slug>, never the whole mount.");
            case AppNode.Invalid { Reason: var reason }:
                return Invalid<FsRemoveResult>(reason);
            case AppNode.Entry { Relative: AppManifest.FileName } entry when File.Exists(entry.Full):
                return Invalid<FsRemoveResult>(
                    $"{AppManifest.FileName} is what makes /{entry.App.Slug} an app: remove the app's directory to delete the app.");
            case AppNode.Entry entry when File.Exists(entry.Full) || Directory.Exists(entry.Full):
                if (WriteRefusal<FsRemoveResult>(entry) is { } refused)
                {
                    return refused;
                }

                if (File.Exists(entry.Full))
                {
                    File.Delete(entry.Full);
                }
                else
                {
                    Directory.Delete(entry.Full, recursive: true);
                }

                return Removed(path, "deleted");
            default:
                return NotFound<FsRemoveResult>(path);
        }
    }

    public override async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsMoveResult>();
        }

        var source = await ResolveAsync(sourcePath, ct);
        var destination = await ResolveAsync(destinationPath, ct);
        switch (source, destination)
        {
            case (AppNode.AppDirectory { App: var app }, AppNode.Missing { Relative: "", Slug: var slug }):
                return await RenameAsync(app, slug, sourcePath, destinationPath, ct);
            case (AppNode.AppDirectory, AppNode.AppDirectory):
                return FsError.AlreadyExists<FsMoveResult>($"'{destinationPath}' is already one of the person's apps.");
            case (AppNode.AppDirectory, _):
                return Invalid<FsMoveResult>("An app's directory moves only to /<new-slug>, which renames the app.");
            case (AppNode.Invalid invalid, _):
                return Invalid<FsMoveResult>(invalid.Reason);
            case (_, AppNode.Invalid invalid):
                return Invalid<FsMoveResult>(invalid.Reason);
            case (AppNode.Action { Name: var action }, _):
                return ExecutableOnly<FsMoveResult>(sourcePath, action);
            case (AppNode.Entry from, AppNode.Entry to) when File.Exists(from.Full) || Directory.Exists(from.Full):
                if (from.App.Id != to.App.Id)
                {
                    return Invalid<FsMoveResult>("A move stays within one app: copy between apps, then delete the source.");
                }

                if (from.Relative == AppManifest.FileName || to.Relative == AppManifest.FileName)
                {
                    return Invalid<FsMoveResult>($"{AppManifest.FileName} stays at the app's root: edit it in place.");
                }

                if ((WriteRefusal<FsMoveResult>(from) ?? WriteRefusal<FsMoveResult>(to)) is { } refused)
                {
                    return refused;
                }

                if (File.Exists(to.Full) || Directory.Exists(to.Full))
                {
                    return FsError.AlreadyExists<FsMoveResult>($"'{destinationPath}' already exists.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(to.Full)!);
                if (File.Exists(from.Full))
                {
                    File.Move(from.Full, to.Full);
                }
                else
                {
                    Directory.Move(from.Full, to.Full);
                }

                return new FsResult<FsMoveResult>.Ok(new FsMoveResult
                {
                    Status = "success", Message = "moved", Source = sourcePath, Destination = destinationPath
                });
            case (AppNode.Entry, AppNode.Missing { Slug: var slug }):
                return NoSuchApp<FsMoveResult>(slug);
            case (AppNode.Entry, _):
                return Invalid<FsMoveResult>($"'{destinationPath}' is not a path inside one of the person's apps.");
            default:
                return NotFound<FsMoveResult>(sourcePath);
        }
    }

    public override async Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsCopyResult>();
        }

        var source = await ResolveAsync(sourcePath, ct);
        var destination = await ResolveAsync(destinationPath, ct);
        switch (source, destination)
        {
            case (AppNode.Invalid invalid, _):
                return Invalid<FsCopyResult>(invalid.Reason);
            case (_, AppNode.Invalid invalid):
                return Invalid<FsCopyResult>(invalid.Reason);
            case (AppNode.Action { Name: var action }, _):
                return ExecutableOnly<FsCopyResult>(sourcePath, action);
            case (AppNode.AppDirectory, _):
                return Invalid<FsCopyResult>("A whole app is not copied: copy the files it holds into another app.");
            case (AppNode.Entry from, AppNode.Entry to) when File.Exists(from.Full) || Directory.Exists(from.Full):
                if (to.Relative == AppManifest.FileName)
                {
                    return Invalid<FsCopyResult>($"{AppManifest.FileName} is written as text, so it is checked before it is kept.");
                }

                if (WriteRefusal<FsCopyResult>(to) is { } refused)
                {
                    return refused;
                }

                var files = File.Exists(from.Full)
                    ? [(Source: from.Full, Relative: "")]
                    : Directory.EnumerateFiles(from.Full, "*", SearchOption.AllDirectories)
                        .Select(file => (Source: file, Relative: Path.GetRelativePath(from.Full, file).Replace('\\', '/')))
                        .ToList();
                if (files.Any(f => IsUnder(f.Relative, "node_modules") || IsUnder(f.Relative, ".git")))
                {
                    return NodeModules<FsCopyResult>();
                }

                if ((File.Exists(to.Full) || Directory.Exists(to.Full)) && !overwrite)
                {
                    return FsError.AlreadyExists<FsCopyResult>($"'{destinationPath}' already exists: set overwrite to replace it.");
                }

                if (!createDirectories && !Directory.Exists(Path.GetDirectoryName(to.Full)))
                {
                    return NotFound<FsCopyResult>(Path.GetDirectoryName(destinationPath)!.Replace('\\', '/'));
                }

                var bytes = files.Sum(f => new FileInfo(f.Source).Length);
                if (OverQuota<FsCopyResult>(to.App, bytes, 0) is { } quota)
                {
                    return quota;
                }

                foreach (var (file, relative) in files)
                {
                    var target = relative.Length == 0 ? to.Full : Path.Combine(to.Full, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }

                return new FsResult<FsCopyResult>.Ok(new FsCopyResult
                {
                    Status = "copied", Source = sourcePath, Destination = destinationPath, Bytes = bytes
                });
            case (AppNode.Entry, AppNode.Missing { Slug: var slug }):
                return NoSuchApp<FsCopyResult>(slug);
            case (AppNode.Entry, _):
                return Invalid<FsCopyResult>($"'{destinationPath}' is not a path inside one of the person's apps.");
            default:
                return NotFound<FsCopyResult>(sourcePath);
        }
    }

    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(string path, long offset, int length, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsBlobReadResult>();
        }

        return await ResolveAsync(path, ct) switch
        {
            AppNode.Action { Name: var action } => ExecutableOnly<FsBlobReadResult>(path, action),
            AppNode.Entry { Full: var full, Base: var root } when File.Exists(full) =>
                new BlobReadTool(root).Run(full, offset, length),
            AppNode.Invalid { Reason: var reason } => Invalid<FsBlobReadResult>(reason),
            _ => NotFound<FsBlobReadResult>(path)
        };
    }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long offset = 0;
        while (true)
        {
            if (!(await ReadBlobAsync(path, offset, BlobReadTool.MaxChunkSizeBytes, ct)).TryGetValue(out var chunk, out var error))
            {
                throw new FileSystemOperationException(error);
            }

            var bytes = Convert.FromBase64String(chunk.ContentBase64);
            if (bytes.Length > 0)
            {
                offset += bytes.Length;
                yield return bytes;
            }

            if (chunk.Eof || bytes.Length == 0)
            {
                yield break;
            }
        }
    }

    // How a binary file arrives: a copy from another mount, or a sandbox command writing anything that
    // is not text, streams its bytes here a chunk at a time.
    public override async Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsBlobWriteResult>();
        }

        switch (await ResolveAsync(path, ct))
        {
            case AppNode.Missing { Slug: var slug }:
                return NoSuchApp<FsBlobWriteResult>(slug);
            case AppNode.Action { Name: var action }:
                return ExecutableOnly<FsBlobWriteResult>(path, action);
            case AppNode.Invalid { Reason: var reason }:
                return Invalid<FsBlobWriteResult>(reason);
            case AppNode.Entry { Relative: AppManifest.FileName }:
                return Invalid<FsBlobWriteResult>($"{AppManifest.FileName} is written as text, so it is checked before it is kept.");
            case AppNode.Entry entry:
                if (WriteRefusal<FsBlobWriteResult>(entry) is { } refused)
                {
                    return refused;
                }

                var added = (long)(contentBase64.Length / 4 * 3);
                var replaced = offset == 0 ? ExistingLength(entry.Full) : 0;
                if (OverQuota<FsBlobWriteResult>(entry.App, added, replaced) is { } quota)
                {
                    return quota;
                }

                return new BlobWriteTool(store.Repository(entry.App).WorkTree)
                    .Run(entry.Full, contentBase64, offset, overwrite, createDirectories)
                    .Map(written => written with { Path = path });
            default:
                return Invalid<FsBlobWriteResult>($"Write a file inside an app: /<slug>/<file> (got '{path}').");
        }
    }

    public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        long offset = 0;
        var first = true;
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            await WriteAsync(chunk.ToArray(), offset);
            offset += chunk.Length;
            first = false;
        }

        if (first)
        {
            await WriteAsync([], 0);
        }

        return offset;

        async Task WriteAsync(byte[] bytes, long at)
        {
            if (!(await WriteBlobAsync(path, Convert.ToBase64String(bytes), at, overwrite, createDirectories, ct))
                .TryGetValue(out _, out var error))
            {
                throw new FileSystemOperationException(error);
            }
        }
    }

    public override async Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        if (_author is null)
        {
            return Nobody<FsExecResult>();
        }

        var app = await ResolveAsync(path, ct) switch
        {
            AppNode.AppDirectory { App: var dir } => dir,
            AppNode.Action { App: var owner } => owner,
            _ => null
        };
        if (app is null)
        {
            return Fail<FsExecResult>(ToolError.Codes.UnsupportedOperation,
                $"exec runs an app's actions in its directory, /<slug>: ./{AppActions.Publish}, ./{AppActions.History}, ./{AppActions.Rollback}, ./{AppActions.Check} (got '{path}').");
        }

        var words = command.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length > 1 && word[0] == '\'' && word[^1] == '\'' ? word[1..^1] : word)
            .ToList();
        var action = words.Count == 0 ? "" : WithoutDotSlash(words[0]);
        var args = words.Skip(1).ToList();
        return await _actions.RunAsync(app, path, action, args, new AppActions.Caller(_agentId, _household, _callToken), ct);
    }

    private async Task<FsResult<FsCreateResult>> CreateAppAsync(
        Account author, string slug, string path, string manifest, CancellationToken ct)
    {
        if (!Slugs.IsValid(slug) || slug.Length > SlugLength)
        {
            return Invalid<FsCreateResult>(
                $"'{slug}' is no app slug: 1–{SlugLength} lowercase letters, digits and inner hyphens (it is part of the app's hostname).");
        }

        if (await ManifestRefusalAsync<FsCreateResult>(manifest, ct) is { } refused)
        {
            return refused;
        }

        return await store.OneAtATimeAsync(author.TenantId, async () =>
        {
            var household = await store.ListAsync(author.TenantId, ct);
            if (household.Any(app => app.Author == author && app.Slug == slug))
            {
                return FsError.AlreadyExists<FsCreateResult>($"'/{slug}' is already one of the person's apps.");
            }

            if (household.Count >= store.Quotas.AppsPerHousehold)
            {
                return Quota<FsCreateResult>("apps per household", store.Quotas.AppsPerHousehold,
                    "Delete an app the household no longer uses before creating another.");
            }

            if (System.Text.Encoding.UTF8.GetByteCount(manifest) > store.Quotas.BytesPerRepository)
            {
                return Quota<FsCreateResult>("bytes per repository", store.Quotas.BytesPerRepository, "Keep the manifest small.");
            }

            var app = await store.CreateAsync(author.TenantId, author, _agentId!, slug, manifest, ct);
            return new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created",
                FilePath = path,
                Size = System.Text.Encoding.UTF8.GetByteCount(manifest).ToString(),
                Lines = manifest.Split('\n').Length,
                Note = $"Created the app {slug} (id {app.Id}). Write its files under /apps/{slug}/, then run ./{AppActions.Publish} there "
                       + $"to serve it at {BuiltAppId.Link(slug, app.Id, appsDomain)}."
            });
        }, ct);
    }

    private async Task<FsResult<FsMoveResult>> RenameAsync(
        BuiltApp app, string slug, string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (!Slugs.IsValid(slug) || slug.Length > SlugLength)
        {
            return Invalid<FsMoveResult>(
                $"'{slug}' is no app slug: 1–{SlugLength} lowercase letters, digits and inner hyphens (it is part of the app's hostname).");
        }

        return await store.OneAtATimeAsync(app.HouseholdId, async () =>
        {
            if ((await store.ListAsync(app.HouseholdId, app.Author, ct)).Any(other => other.Slug == slug))
            {
                return FsError.AlreadyExists<FsMoveResult>($"'/{slug}' is already one of the person's apps.");
            }

            await store.SaveAsync(app with { Slug = slug }, ct);
            return new FsResult<FsMoveResult>.Ok(new FsMoveResult
            {
                Status = "success",
                Message = $"renamed the app; it is served at {BuiltAppId.Link(slug, app.Id, appsDomain)} and its old link still opens it",
                Source = sourcePath,
                Destination = destinationPath
            });
        }, ct);
    }

    // Only the household's own agents build apps: a person's registered agent resolves for its owner
    // alone, so nobody else could ever read through it. The turn's agent is checked against the
    // household's agent names, as the call's household proves them.
    private async Task<FsResult<T>?> ManifestRefusalAsync<T>(string manifest, CancellationToken ct) where T : class
    {
        if (_household is null || _agentId is null
            || !(await _household.AgentNamesAsync(ct)).Agents.Any(agent => agent.Id == _agentId))
        {
            return Fail<T>(ToolError.Codes.PermissionDenied,
                $"Built apps are made by the household's own agents, and this turn's agent ('{_agentId}') is not one of "
                + "them: a person's registered agent cannot build or change an app.",
                "Ask one of the household's own agents to build the app.");
        }

        return AppManifest.Parse(manifest) is AppManifestParse.Refused { Problems: var problems }
            ? Invalid<T>($"{AppManifest.FileName} was not kept; it breaks these rules:\n- {string.Join("\n- ", problems)}")
            : null;
    }

    // An app the household shares as an app space is the household's to stop sharing before it is anyone's
    // to delete: the spaces section, as the call's household proves it, names the space an owner removes
    // first. Whether it is shared that cannot be told just now deletes nothing.
    private async Task<FsResult<T>?> SharedRefusalAsync<T>(BuiltApp app, CancellationToken ct) where T : class
    {
        if (_household is null)
        {
            return Nobody<T>();
        }

        return await AppSharing.OfAsync(_household, app, ct) switch
        {
            AppSharing.Read { SharedAs: null } => null,
            AppSharing.Read { SharedAs: { } space } => Fail<T>(ToolError.Codes.PermissionDenied,
                $"The app {app.Slug} is the household's space '{space.Name}', so it is not deleted while it is shared.",
                $"Ask an owner of the household to remove the space '{space.Name}' on the spaces page first; then it can be deleted."),
            AppSharing.Unreadable => Fail<T>(ToolError.Codes.TransientDependency,
                $"Whether the household shares the app {app.Slug} as a space could not be checked just now, so it was not deleted.",
                "Try again in a moment."),
            // Refused is closed, as an origin is to anyone but the author: asking again changes nothing.
            _ => Fail<T>(ToolError.Codes.PermissionDenied,
                $"The accounts service would not tell this server whether the household shares the app {app.Slug} as a space, "
                + "so it was not deleted.",
                "Tell the person; the household's operator has to look at why the apps server's word is refused.")
        };
    }

    private FsResult<T>? OverQuota<T>(BuiltApp app, long added, long replaced) where T : class =>
        store.SizeOf(app) - replaced + added > store.Quotas.BytesPerRepository
            ? Quota<T>("bytes per repository", store.Quotas.BytesPerRepository,
                "Remove what the app no longer needs (old build output, large images) before adding more.")
            : null;

    internal static FsResult<T> Quota<T>(string limit, long value, string hint) where T : class =>
        Fail<T>(ToolError.Codes.PermissionDenied, $"Refused: the limit of {limit} is {value}, and this would pass it.", hint);

    private static FsResult<T>? WriteRefusal<T>(AppNode.Entry entry) where T : class
    {
        if (IsUnder(entry.Relative, "node_modules"))
        {
            return NodeModules<T>();
        }

        if (IsUnder(entry.Relative, ".git"))
        {
            return Fail<T>(ToolError.Codes.PermissionDenied,
                "Nothing named .git is written into an app: its history is the apps server's, kept outside the working tree.",
                $"./{AppActions.History} lists the app's commits.");
        }

        var top = entry.Relative.Split('/')[0];
        return _reserved.Contains(top)
            ? Fail<T>(ToolError.Codes.PermissionDenied,
                $"'{top}' at an app's root is the apps server's own, never a file of the app's.")
            : null;
    }

    private static FsResult<T> NodeModules<T>() where T : class =>
        Fail<T>(ToolError.Codes.PermissionDenied,
            "Writes under node_modules/ are refused: a dependency tree never enters an app's repository.",
            "Build in the sandbox and copy only the build output (such as dist/) into the app, naming it as root in app.json.");

    private static bool IsUnder(string relative, string segment) =>
        relative.Split('/').Contains(segment, StringComparer.Ordinal);

    // Every path of an app as the mount lists it, mount-relative and without a leading slash, with where
    // it is kept: the app's directory, its action files, its working tree (never a .git anywhere in it),
    // the runtime errors its pages reported and what its last check saw.
    private IReadOnlyList<ListedPath> Listing(BuiltApp app)
    {
        var tree = store.Repository(app).WorkTree;
        var errors = store.ErrorsFile(app);
        var checksDirectory = store.ChecksDirectory(app);
        var latest = Path.Combine(checksDirectory, AppChecks.LatestDirectory);
        var latestPath = $"{app.Slug}/{ChecksOutput}/{AppChecks.LatestDirectory}";
        return
        [
            new ListedPath(app.Slug, tree, IsDirectory: true),
            .. AppActions.Actions.Select(action => new ListedPath($"{app.Slug}/{action}", "", IsDirectory: false)),
            .. Entries(app.Slug, tree),
            .. File.Exists(errors) ? [new ListedPath($"{app.Slug}/{ErrorsOutput}", errors, IsDirectory: false)] : Array.Empty<ListedPath>(),
            .. Directory.Exists(latest)
                ? [
                    new ListedPath($"{app.Slug}/{ChecksOutput}", checksDirectory, IsDirectory: true),
                    new ListedPath(latestPath, latest, IsDirectory: true),
                    .. Entries(latestPath, latest)
                ]
                : Array.Empty<ListedPath>()
        ];
    }

    // A walk that never descends into a .git, so a filter over a flat enumeration would not do.
    private static IEnumerable<ListedPath> Entries(string prefix, string tree)
    {
        var pending = new Stack<string>([tree]);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().Where(entry => entry.Name != ".git"))
            {
                var isDirectory = entry is DirectoryInfo;
                if (isDirectory)
                {
                    pending.Push(entry.FullName);
                }

                yield return new ListedPath(
                    $"{prefix}/{Path.GetRelativePath(tree, entry.FullName).Replace('\\', '/')}", entry.FullName, isDirectory);
            }
        }
    }

    // A path the mount lists (mount-relative, no leading slash) and where it is kept on disk ("" for an
    // action file, which is kept nowhere).
    private sealed record ListedPath(string Path, string Full, bool IsDirectory);

    private async Task<AppNode> ResolveAsync(string path, CancellationToken ct)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return new AppNode.Root();
        }

        if (segments.Any(s => s is "." or ".."))
        {
            return new AppNode.Invalid("A path in /apps never climbs with . or ..");
        }

        var relative = string.Join('/', segments[1..]);
        var app = (await store.ListAsync(_author!.TenantId, _author, ct)).FirstOrDefault(a => a.Slug == segments[0]);
        return (app, segments) switch
        {
            (null, _) => new AppNode.Missing(segments[0], relative),
            (_, [_]) => new AppNode.AppDirectory(app),
            (_, [_, var action]) when AppActions.Actions.Contains(action) => new AppNode.Action(app, action),
            // What the server keeps beside the tree, shown where the agent looks for it: every write there is
            // refused as a reserved name's.
            (_, [_, ChecksOutput, ..]) => new AppNode.Entry(app, relative,
                Path.Combine([store.ChecksDirectory(app), .. segments[2..]]), store.ChecksDirectory(app)),
            (_, [_, ErrorsOutput]) => new AppNode.Entry(app, relative, store.ErrorsFile(app), Path.GetDirectoryName(store.ErrorsFile(app))!),
            _ => new AppNode.Entry(app, relative, Path.Combine(store.Repository(app).WorkTree, relative), store.Repository(app).WorkTree)
        };
    }

    private abstract record AppNode
    {
        public sealed record Root : AppNode;

        public sealed record AppDirectory(BuiltApp App) : AppNode;

        public sealed record Action(BuiltApp App, string Name) : AppNode;

        // Full is where it is kept, under Base: the working tree, or the checks' own directory.
        public sealed record Entry(BuiltApp App, string Relative, string Full, string Base) : AppNode;

        // A slug the person has no app by, with what was asked under it.
        public sealed record Missing(string Slug, string Relative) : AppNode;

        public sealed record Invalid(string Reason) : AppNode;
    }

    private static long ExistingLength(string full) => File.Exists(full) ? new FileInfo(full).Length : 0;

    private static FsResult<FsInfoResult> Info(string path, bool exists, bool? isDirectory) =>
        new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = exists, Path = path, IsDirectory = isDirectory });

    private static FsResult<FsRemoveResult> Removed(string path, string message) =>
        new FsResult<FsRemoveResult>.Ok(new FsRemoveResult { Status = "deleted", Message = message, OriginalPath = path, TrashPath = "" });

    private static FsResult<T> NoSuchApp<T>(string slug) where T : class =>
        Fail<T>(ToolError.Codes.NotFound, $"The person has no app '{slug}'.",
            $"Create it by writing /apps/{slug}/{AppManifest.FileName} first.");

    private const string ForNobody =
        "This call acts for nobody, so it has no apps: /apps answers for the person whose turn it is.";

    private static FsResult<T> Nobody<T>() where T : class => Fail<T>(ToolError.Codes.PermissionDenied, ForNobody);
}