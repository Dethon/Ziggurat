using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Downloads.Vfs;

// What an operation is about to do to one path on the media mount. Every operation names one of
// these before it acts, and an operation with two ends names one per end.
public enum DownloadsIntent
{
    TextRead,
    ByteRead,
    Land,
    MoveOut,
    Delete
}

// Overlays download semantics on the media filesystem's downloads/ subtree: every live
// download surfaces a rendered downloads/<id>/status.json, and deleting downloads/<id> cancels
// the download and cleans up its files. A download owns a path only while it is live, so a
// status.json nothing owns is a leftover the disk serves — which is why liveness, and never a
// path's spelling, is what RefuseAsync asks about.
public sealed class DownloadsOverlay(
    IDownloadClient downloadClient,
    IDownloadRoutingStore routingStore,
    IFileSystemClient fileSystemClient,
    LibraryPathConfig libraryPath)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // True when this path and a live download's directory overlap: the directory itself, any
    // ancestor of it (moving a parent takes the directory with it), and anything under it. Whatever
    // lands in there is destroyed the moment the download is cancelled, and a download the client
    // still lists can still be cancelled however far along it is.
    private Task<bool> TouchesLiveDownloadAsync(string path, CancellationToken ct) =>
        OverlapsADownloadAsync(path, _ => true, ct);

    // The same overlap, asked only of the downloads that are still writing. A finished download is
    // not: its files stopped changing, and moving them into the library is what the completion alert
    // asks the agent to do. qBittorrent keeps a finished torrent listed while it seeds, so "the
    // client still lists it" would refuse that move forever — the download would have to be
    // cancelled, which deletes the very files being organised. Only Completed frees them: a paused
    // download resumes into its files, and a failed one left a partial the agent should cancel
    // rather than file away.
    private Task<bool> TouchesUnfinishedDownloadAsync(string path, CancellationToken ct) =>
        OverlapsADownloadAsync(path, i => i.State is not DownloadState.Completed, ct);

    private async Task<bool> OverlapsADownloadAsync(
        string path, Func<DownloadItem, bool> which, CancellationToken ct)
    {
        // The same canonical spelling the classifier uses, so 'downloads/./42' overlaps the live
        // download it names instead of reading as an unrelated string.
        if (DownloadsPath.Canonicalize(ToMountRelative(path)) is not { } candidate)
        {
            return false;
        }

        var items = await downloadClient.GetDownloadItems(ct);
        return items
            .Where(which)
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}")
            .Any(dir => candidate.Length == 0
                        || dir.Equals(candidate, StringComparison.Ordinal)
                        || dir.StartsWith(candidate + "/", StringComparison.Ordinal)
                        || candidate.StartsWith(dir + "/", StringComparison.Ordinal));
    }

    // The one question every operation on this mount asks before it acts: may I do this to this
    // path? Null means yes. One path per call, so a refusal always names the path that offended,
    // and one reason per intent, so two operations can never disagree about the same path.
    public async Task<ToolErrorResult?> RefuseAsync(DownloadsIntent intent, string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        return intent switch
        {
            // The only text on this mount is a status file; the media itself is bytes. A leftover
            // status file is still a status file by name, and reads as the ordinary file it is.
            DownloadsIntent.TextRead when node.Kind != DownloadNodeKind.StatusFile => TextReadRefusal(),
            DownloadsIntent.ByteRead when await IsLiveStatusFileAsync(node, ct) => ByteReadRefusal(path),
            DownloadsIntent.Land when await TouchesLiveDownloadAsync(path, ct) => LandRefusal(path),
            DownloadsIntent.MoveOut when await TouchesUnfinishedDownloadAsync(path, ct) => MoveOutRefusal(path),
            // Asked after the boundary, so a download still running is told the more useful thing.
            // What is left is a finished download's status file: still rendered, so there is nothing
            // on disk for the move to take.
            DownloadsIntent.MoveOut when await IsLiveStatusFileAsync(node, ct) => StatusFileMoveRefusal(path),
            DownloadsIntent.Delete => await DeleteRefusalAsync(node, path, ct),
            _ => null
        };
    }

    private static ToolErrorResult TextReadRefusal() => Error(
        ToolError.Codes.UnsupportedOperation,
        $"fs_read on the {MediaLibraryDiskFileSystem.Name} filesystem only reads "
        + $"{MediaFilesystem.DownloadsSubdir}/<id>/{DownloadsPath.StatusFileName}.",
        $"Use fs_blob_read for the raw bytes of any other file on {MediaLibraryDiskFileSystem.Name}.");

    private static ToolErrorResult ByteReadRefusal(string path) => Error(
        ToolError.Codes.UnsupportedOperation,
        $"'{path}' is a live download's status file: a view rendered when it is read, not bytes on "
        + "disk. Read it as text with fs_read.",
        "fs_read on this path reports the download's state, progress and eta.");

    // Every way of putting bytes where a live download is carries this one reason, including a
    // write to its status file: that write lands inside the directory too, and "it dies when the
    // download is cancelled" is the more useful thing to say.
    private static ToolErrorResult LandRefusal(string path) => Error(
        ToolError.Codes.UnsupportedOperation,
        $"'{path}' is a live download's directory, holds one, or is inside one; anything placed "
        + "there is removed when the download is cancelled.",
        "Wait for the download to finish, or put the file somewhere outside "
        + $"{MediaFilesystem.DownloadsSubdir}/<id>.");

    // The way out of the boundary, while the download is still writing: it recreates what it lost,
    // so the moved copy is orphaned the moment it lands. Once the download finishes this reason is
    // gone with it, and the files move like any others.
    private static ToolErrorResult MoveOutRefusal(string path) => Error(
        ToolError.Codes.UnsupportedOperation,
        $"'{path}' belongs to a download that has not finished; moving across that boundary would "
        + "leave the download writing into files the move cannot follow.",
        $"Wait for it to finish and move the files then, or delete {MediaFilesystem.DownloadsSubdir}"
        + "/<id> to cancel the download.");

    // A status file is rendered on read for as long as the client owns the id, finished or not, so a
    // move would find nothing on disk to take and answer not_found for a path fs_info, fs_glob and
    // fs_read all report as existing.
    private static ToolErrorResult StatusFileMoveRefusal(string path) => Error(
        ToolError.Codes.UnsupportedOperation,
        $"'{path}' is a download's status file: a view rendered when it is read, not bytes on disk, "
        + "so there is nothing to move.",
        "Move the download's payload files instead; the status file disappears on its own once no "
        + "download owns the id.");

    // Delete's two refusals. Everything it does — the cancel, the routing removal, the leftover
    // recovery — is not here: the rule answers "may I" and never acts.
    private async Task<ToolErrorResult?> DeleteRefusalAsync(DownloadsNode node, string path, CancellationToken ct) =>
        node.Kind switch
        {
            DownloadNodeKind.DownloadDir => null,
            // A live status file is a view of a download the agent meant to leave running; a
            // leftover one is an ordinary file the disk removes.
            DownloadNodeKind.StatusFile => await IsLiveStatusFileAsync(node, ct)
                ? Error(
                    ToolError.Codes.UnsupportedOperation,
                    $"'{path}' is read-only while a download owns it: it is the rendered view of a "
                    + "live download, and removing it would dismantle part of a download that is "
                    + "still running.",
                    $"Delete {MediaFilesystem.DownloadsSubdir}/<id> to cancel the download itself.")
                : null,
            _ => Error(
                ToolError.Codes.UnsupportedOperation,
                $"fs_delete on the {MediaLibraryDiskFileSystem.Name} filesystem only removes "
                + $"download directories ({MediaFilesystem.DownloadsSubdir}/<id>) and leftover "
                + $"{DownloadsPath.StatusFileName} files no live download owns.",
                "Nothing else on this filesystem can be deleted; the library is not scratch space.")
        };

    // Reached once the rule has allowed a text read, so the path is a status file: either the
    // rendered view of a live download or a leftover the disk owns. A caller that skipped the rule
    // is answered not_found rather than crashing — there is nothing here to render for it.
    public async Task<FsResult<FsReadResult>> ReadAsync(string path, CancellationToken ct)
    {
        if (ParseNode(path) is not { Kind: DownloadNodeKind.StatusFile, Id: { } id })
        {
            return FsError.NotFound<FsReadResult>(path);
        }

        return await downloadClient.GetDownloadItem(id, ct) is { } item
            ? Read(path, RenderStatus(item))
            : await ReadLeftoverAsync(path, id, ct);
    }

    // Virtual AND currently owned by a live download — the distinction that decides whether a
    // status.json path is the rendered view or a leftover real file the disk should serve.
    private async Task<bool> IsLiveStatusFileAsync(DownloadsNode node, CancellationToken ct) =>
        node is { Kind: DownloadNodeKind.StatusFile, Id: { } id }
        && await downloadClient.GetDownloadItem(id, ct) is not null;

    // No live download owns the id, but info and delete already treat a real file left at this
    // path as the disk's; reads must agree, or the leftover is visible and removable yet
    // unreadable.
    private async Task<FsResult<FsReadResult>> ReadLeftoverAsync(string path, int id, CancellationToken ct)
    {
        var file = Path.Combine(DiskDir(id), DownloadsPath.StatusFileName);
        return File.Exists(file)
            ? Read(path, await File.ReadAllTextAsync(file, ct))
            : new FsResult<FsReadResult>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));
    }

    private static FsResult<FsReadResult> Read(string path, string content) =>
        new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });

    public async Task<FsResult<FsInfoResult>?> TryInfoAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        switch (node.Kind)
        {
            // Only a live download has a virtual status file. With no item owning the id the
            // overlay owns nothing here, so the disk underneath answers — otherwise a real file
            // left at this path is reported as not existing and can never be found.
            case DownloadNodeKind.StatusFile when await downloadClient.GetDownloadItem(node.Id!.Value, ct) is { } item:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult
                {
                    Exists = true,
                    Path = path,
                    IsDirectory = false,
                    Size = RenderStatus(item).Length
                });
            case DownloadNodeKind.DownloadDir when await downloadClient.GetDownloadItem(node.Id!.Value, ct) is not null:
                return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
            default:
                return null;
        }
    }

    // The overlay half of a media glob, under the same two guards as every other mount: the shared
    // prologue answers the invalid-argument envelope for a pattern past the brace-expansion cap, and
    // a pathological pattern that backtracks while matching ends as the timeout envelope instead of
    // an exception out of fs_glob. Candidates are library-root-relative, the same convention the
    // disk glob results use, and the scope's matcher already carries basePath.
    public async Task<FsResult<IReadOnlyList<string>>> GlobEntriesAsync(
        string? basePath, string pattern, CancellationToken ct)
    {
        if (!GlobScope.Create(basePath, pattern).TryGetValue(out var scope, out var scopeError))
        {
            return new FsResult<IReadOnlyList<string>>.Err(scopeError);
        }

        var items = await downloadClient.GetDownloadItems(ct);

        var dirs = items
            .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}")
            .Where(scope.Matches)
            .Select(c => c + "/");

        IEnumerable<string> files = scope.DirsOnly
            ? []
            : items
                .Select(i => $"{MediaFilesystem.DownloadsSubdir}/{i.Id}/{DownloadsPath.StatusFileName}")
                .Where(scope.Matches);

        try
        {
            return new FsResult<IReadOnlyList<string>>.Ok(
                dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
        }
        catch (RegexMatchTimeoutException)
        {
            return new FsResult<IReadOnlyList<string>>.Err(GlobRegex.TimedOut(pattern));
        }
    }

    // Reached only once the rule has allowed a delete, so this is either a download directory —
    // whose delete is the download's cancel — or a leftover status file, which is null: a real file
    // the disk must be allowed to remove.
    public async Task<FsResult<FsRemoveResult>?> TryDeleteAsync(string path, CancellationToken ct)
    {
        var node = ParseNode(path);
        if (node.Kind != DownloadNodeKind.DownloadDir)
        {
            return null;
        }

        var id = node.Id!.Value;
        if (await downloadClient.GetDownloadItem(id, ct) is not null)
        {
            // Deliberately best-effort / non-transactional: a Cleanup failure throws and aborts
            // before the housekeeping steps (so we never orphan routing/files for a download
            // that is still running), while the on-disk dir removal is swallowed because
            // leftover/missing files must not undo a successful manager-side cleanup.
            await downloadClient.Cleanup(id, ct);
            await routingStore.RemoveAsync(id, ct);
            await RemoveDownloadDirectoryAsync(id, ct);
            return Removed(path, "Download cancelled and its files removed.");
        }

        if (Directory.Exists(DiskDir(id)))
        {
            // Leftover recovery: no torrent owns the id, but the directory survived a crash or
            // an external removal. Here the dir removal IS the point, so failures propagate.
            await fileSystemClient.RemoveDirectory(DiskDir(id), ct);
            await routingStore.RemoveAsync(id, ct);
            return Removed(path, "Leftover download directory removed.");
        }

        return new FsResult<FsRemoveResult>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));
    }

    private async Task RemoveDownloadDirectoryAsync(int id, CancellationToken ct)
    {
        try
        {
            await fileSystemClient.RemoveDirectory(DiskDir(id), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort: a missing or undeletable directory does not undo the cleanup.
        }
    }

    private string DiskDir(int id) =>
        Path.Combine(libraryPath.BaseLibraryPath, MediaFilesystem.DownloadsSubdir, id.ToString());

    // Tools receive mount-relative paths from the agent, but the legacy disk tools also accept
    // absolute paths under the library root — normalize those before classifying.
    private DownloadsNode ParseNode(string path)
    {
        var node = DownloadsPath.Parse(path);
        return node.Kind == DownloadNodeKind.Other ? DownloadsPath.Parse(ToMountRelative(path)) : node;
    }

    // An absolute path under the library root, spelled the way the agent addresses the mount.
    // Anything else — already relative, or rooted somewhere else entirely — is left alone.
    private string ToMountRelative(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return path;
        }

        var root = Path.GetFullPath(libraryPath.BaseLibraryPath);
        var full = Path.GetFullPath(path);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.Ordinal)
            ? Path.GetRelativePath(root, full).Replace('\\', '/')
            : path;
    }

    private static string RenderStatus(DownloadItem item) => JsonSerializer.Serialize(new
    {
        id = item.Id,
        title = item.Title,
        state = item.State.ToString(),
        progressPercent = Math.Round(item.Progress * 100, 2),
        sizeMb = item.Size,
        downSpeedMbps = item.DownSpeed,
        upSpeedMbps = item.UpSpeed,
        etaMinutes = item.Eta
    }, _json);

    private static FsResult<FsRemoveResult> Removed(string path, string message) =>
        new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = message,
            OriginalPath = path,
            TrashPath = ""
        });

    private static ToolErrorResult Error(string code, string message, string? hint = null) =>
        new() { ErrorCode = code, Message = message, Retryable = false, Hint = hint };
}