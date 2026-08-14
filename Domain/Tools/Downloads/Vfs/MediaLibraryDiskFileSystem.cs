using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Files;

namespace Domain.Tools.Downloads.Vfs;

// The media library: a disk root with the downloads overlay layered on top of the same directory.
// Every active download surfaces a virtual downloads/<id>/status.json that is not on disk, and
// deleting downloads/<id> cancels the download rather than trashing a directory. Payload files
// inside a download directory stay plain disk entries, which is why this extends the disk root
// instead of replacing it.
public sealed class MediaLibraryDiskFileSystem(
    IFileSystemClient client,
    LibraryPathConfig root,
    DownloadsOverlay downloads) : DiskFileSystem(Name, Mount(root), client, root)
{
    public const string Name = "media";

    // Read off the parameter here rather than in a member body: the same value goes to the base
    // constructor, and capturing the parameter itself would leave two copies of the disk root.
    private readonly string _rootPath = root.BaseLibraryPath;

    // Composed here rather than handed in like the generic disk root's: this type is the media
    // library, so its prose belongs with it, and it already holds the path that text names.
    private static string Mount(LibraryPathConfig root) =>
        $"Media library ({root.BaseLibraryPath}) — books, audiobooks, and other downloaded media. "
        + "Read/list focused; treat writes as organisational only. Does NOT support fs_exec. Active "
        + $"downloads live under {MediaFilesystem.DownloadsDir}/<id>/: a status.json rendered on "
        + "read reports live state/progress/eta, and deleting the <id> directory cancels the "
        + "download and cleans up its files. Once a download ends, what it left behind is ordinary "
        + "files.";

    public override string DescribeRead =>
        $"Read a download's virtual status file ({MediaFilesystem.DownloadsSubdir}/<id>/status.json "
        + "— live state, progress, eta). Other media files are not text-readable; use fs_blob_read "
        + "for raw bytes.";

    public override string DescribeDelete =>
        $"Delete a download directory ({MediaFilesystem.DownloadsSubdir}/<id>): cancels the torrent "
        + "task and cleans up its files. Also removes leftovers whose torrent is already gone: the "
        + "download directory, or a real status.json file no live download owns. Other media paths "
        + "cannot be deleted.";

    // The only text on this mount is the overlay's rendered status file; the media itself is bytes.
    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        await RefuseAsync<FsReadResult>(DownloadsIntent.TextRead, path, ct)
        ?? await downloads.ReadAsync(path, ct);

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var disk = await base.GlobAsync(basePath, pattern, ct);
        // An active download's directory can predate its files on disk (queued, still fetching
        // metadata), and info already reports it as existing — so the disk's not_found is not this
        // mount's answer until the overlay has had its say. Every other disk error stands.
        if (!disk.TryGetValue(out var entries, out var diskError))
        {
            if (diskError.ErrorCode != ToolError.Codes.NotFound)
            {
                return disk;
            }

            entries = new FsGlobResult { Entries = [], Truncated = false, Total = 0 };
        }

        // The overlay matches mount-relative candidates, so it must see the pattern the disk matcher
        // ran, not the caller's absolute spelling of it — no candidate ever carries the root prefix,
        // so an absolute pattern used to list the real files and omit every virtual status.json.
        // Separators are normalized because virtual paths are always '/'-separated. A pattern
        // outside the glob root matched nothing on disk and owns no download either.
        var scoped = GlobFilesTool.ToMatcherRelative(
            GlobFilesTool.MatcherRoot(_rootPath, basePath), pattern);

        if (scoped is null)
        {
            return disk;
        }

        var overlay = await downloads.GlobEntriesAsync(basePath, scoped.Replace('\\', '/'), ct);
        if (!overlay.TryGetValue(out var virtualEntries, out var overlayError))
        {
            return new FsResult<FsGlobResult>.Err(overlayError);
        }

        // The overlay owning nothing leaves the disk's answer standing — including its not_found.
        return virtualEntries.Count == 0
            ? disk
            : new FsResult<FsGlobResult>.Ok(Merge(entries, virtualEntries));
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        await downloads.TryInfoAsync(path, ct) ?? await base.InfoAsync(path, ct);

    // Delete contributes its two refusals to the rule like every other operation, and keeps its
    // effects — the cancel, the routing removal, the leftover recovery — in the overlay behind it.
    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        await RefuseAsync<FsRemoveResult>(DownloadsIntent.Delete, path, ct)
        ?? await downloads.TryDeleteAsync(path, ct)
        ?? await base.DeleteAsync(path, ct);

    public override string DescribeMove =>
        "Move or rename a file or directory within this filesystem — how a finished download is "
        + $"filed into the library. A download still running refuses it: {MediaFilesystem.DownloadsSubdir}"
        + "/<id>, anything inside it and any directory holding one cannot move until the download "
        + "finishes.";

    // A move has two ends, so it asks the rule twice — once per end, with the intent belonging to
    // that end. The source is asked first, so a move that offends at both ends names the source.
    public override async Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        await RefuseAsync<FsMoveResult>(DownloadsIntent.MoveOut, sourcePath, ct)
        ?? await RefuseAsync<FsMoveResult>(DownloadsIntent.Land, destinationPath, ct)
        ?? await base.MoveAsync(sourcePath, destinationPath, ct);

    // The cross-mount half of the same refusal, and the reason this mount is the only one that
    // registers the check. A move between two mounts never reaches MoveAsync — it streams the
    // payload out and then deletes the source — so the transfer machinery asks the source this
    // first. Both intents that streamed move is made of are asked, because a path that fails
    // either one can never complete it: the way out of an unfinished download's boundary, where the
    // delete is the download's cancel, and the delete itself, which on this mount removes nothing
    // but download directories and leftovers. Answering Allow to a path the tail delete refuses
    // would stream gigabytes and then report a move that left a duplicate on both mounts. One call
    // answers for the whole subtree: the rule overlaps in both directions.
    //
    // The delete refusal is reworded here rather than passed through: the agent sees this envelope
    // on fs_move, where it never asked to delete anything, so a message that opens with fs_delete
    // answers a question it didn't ask. The move is what failed; the delete is why.
    public override async Task<FsResult<FsMoveOutCheckResult>> MoveOutCheckAsync(string path, CancellationToken ct) =>
        await RefuseAsync<FsMoveOutCheckResult>(DownloadsIntent.MoveOut, path, ct)
        ?? await TailDeleteRefusalAsync(path, ct)
        ?? FsMoveOutCheckResult.Allow(path);

    private async Task<FsResult<FsMoveOutCheckResult>?> TailDeleteRefusalAsync(string path, CancellationToken ct) =>
        await downloads.RefuseAsync(DownloadsIntent.Delete, path, ct) is { } refusal
            ? new FsResult<FsMoveOutCheckResult>.Err(refusal with
            {
                Message = $"'{path}' cannot be moved off the {Name} filesystem: a move off this "
                    + "filesystem ends by deleting the source, and that delete is refused. "
                    + refusal.Message,
                Hint = "Use fs_copy instead; the library keeps its file."
            })
            : null;

    public override string DescribeMoveOutCheck =>
        "Refuses to let a path leave this filesystem when the move could not finish: anything a "
        + $"download that has not finished owns ({MediaFilesystem.DownloadsSubdir}/<id>, what is "
        + "inside it, and any directory holding one), and anything fs_delete here refuses, because a "
        + "move off this filesystem ends by deleting the source. What may leave is what fs_delete "
        + $"removes: a download directory and a leftover {DownloadsPath.StatusFileName} no live "
        + "download owns.";

    // A copy reads one end and lands the other, so each end is asked with the intent belonging to
    // it. The read end is the byte read the streamed copy already asks, which is what refuses a
    // live download's status file: it is rendered when read, so the disk has no bytes to copy and
    // the copy would otherwise answer not_found for a path everything else reports as existing.
    // The landing end is the boundary — whatever lands inside a live download's directory is
    // removed the moment the download is cancelled. Unlike a move, the way out is otherwise
    // harmless: the source keeps its file.
    public override async Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        await RefuseAsync<FsCopyResult>(DownloadsIntent.ByteRead, sourcePath, ct)
        ?? await RefuseAsync<FsCopyResult>(DownloadsIntent.Land, destinationPath, ct)
        ?? await base.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct);

    // The rendered view only exists while a download owns the id. A leftover real status.json with
    // no live download is the disk's file — refusing to read it left it visible and removable yet
    // unreadable, so the refusal asks for liveness instead of the path's spelling.
    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct) =>
        await RefuseAsync<FsBlobReadResult>(DownloadsIntent.ByteRead, path, ct)
        ?? await base.ReadBlobAsync(path, offset, length, ct);

    public override async Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
        await RefuseAsync<FsBlobWriteResult>(DownloadsIntent.Land, path, ct)
        ?? await base.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct);

    // The streamed halves of the read and the write, asking the same rule with the same intent as
    // their ranged counterparts — which is what makes the two halves unable to disagree. They throw
    // rather than answer an envelope because that is the shape the chunk contract has, and
    // VfsCopyTool turns the typed exception back into the envelope the ranged half returns.
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (await downloads.RefuseAsync(DownloadsIntent.ByteRead, path, ct) is { } refusal)
        {
            throw new FileSystemOperationException(refusal);
        }

        await foreach (var chunk in base.ReadChunksAsync(path, ct).WithCancellation(ct))
        {
            yield return chunk;
        }
    }

    // The streamed half of the landing refusal: a cross-mount copy-in never calls CopyAsync or
    // WriteBlobAsync, it streams — the typed exception carries the same envelope through.
    public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (await downloads.RefuseAsync(DownloadsIntent.Land, path, ct) is { } refusal)
        {
            throw new FileSystemOperationException(refusal);
        }

        return await base.WriteChunksAsync(path, chunks, overwrite, createDirectories, ct);
    }

    // Every operation on this mount asks the overlay's one rule before it acts, and falls through
    // to the disk beneath when the rule says nothing.
    private async Task<FsResult<T>?> RefuseAsync<T>(DownloadsIntent intent, string path, CancellationToken ct)
        where T : class =>
        await downloads.RefuseAsync(intent, path, ct) is { } refusal ? new FsResult<T>.Err(refusal) : null;

    // The overlay's entries are reserved before the disk's fill the rest, because a live download
    // owns its path here as it does in every other operation on this mount — and there are at most
    // as many of them as there are live downloads. Filling from the disk first meant a directory
    // holding a capful of ordinary files pushed a running download's status file out of its own
    // listing. The coverage the response reports is the disk walk's own: the overlay enumerates a
    // finite in-memory set and walks nothing.
    private static FsGlobResult Merge(FsGlobResult disk, IReadOnlyList<string> virtualEntries)
    {
        var added = virtualEntries.Except(disk.Entries, StringComparer.Ordinal).ToList();
        if (added.Count == 0)
        {
            return disk;
        }

        var reserved = added.Take(GlobFilesTool.FileResultCap).ToList();
        var combined = reserved
            .Concat(disk.Entries.Take(GlobFilesTool.FileResultCap - reserved.Count))
            .Order(StringComparer.Ordinal)
            .ToList();

        return disk with
        {
            Entries = combined,
            Truncated = disk.Truncated || disk.Entries.Count + added.Count > GlobFilesTool.FileResultCap,
            Total = disk.Total + added.Count
        };
    }
}