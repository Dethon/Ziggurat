using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Files;

// A backend over a real directory on disk: the operations any disk root can perform, composed from
// the disk tool classes. Nothing here knows about a particular mount — a root that also serves text
// or runs commands or layers virtual entries on top says so by subclassing.
//
// Read is not here: reading bytes as text needs a rule about which files are text, and that rule is
// what TextDiskFileSystem adds. A plain disk root advertises no fs_read, which is correct — the
// registrar derives the surface from what is overridden.
public class DiskFileSystem(
    string filesystemName,
    string mountDescription,
    IFileSystemClient client,
    LibraryPathConfig root) : FileSystemBackendBase
{
    public override string FilesystemName => filesystemName;

    public override string DescribeMount => mountDescription;

    private readonly GlobFilesTool _glob = new(client, root);
    private readonly MoveTool _move = new(client, root);
    private readonly RemoveTool _remove = new(client, root);
    private readonly CopyTool _copy = new(root.BaseLibraryPath);
    private readonly FileInfoTool _info = new(root.BaseLibraryPath);
    private readonly BlobReadTool _blobRead = new(root.BaseLibraryPath);
    private readonly BlobWriteTool _blobWrite = new(root.BaseLibraryPath);

    public override string DescribeGlob =>
        "Searches for files and directories matching a glob pattern relative to the mount root. "
        + "`*` matches one path segment, `**` recurses, `?` matches one character. Brace alternation "
        + "expands too: `**/*.{jpg,png,gif}` matches any of the listed extensions. A trailing slash "
        + "matches directories only (e.g. `*/`, `src/**/`); otherwise both files and directories "
        + "match, with directory results returned with a trailing slash so you can tell them apart. "
        + "Results are capped at 200 and the walk stops once it has that many, so which 200 come "
        + "back depends on enumeration order. The walk also stops after "
        + $"{DiskWalk.MaxEntriesScanned:N0} entries have been enumerated, whatever it has found by "
        + "then. The response is `{entries, truncated, total, entriesScanned, budgetReached}`: "
        + "`truncated` means more matched than fit, `budgetReached` means the walk stopped before "
        + "the tree ended, and `entriesScanned` says how much of it the answer covers. An empty "
        + "result with `budgetReached` false means nothing matched—refine the pattern; with it "
        + "true, narrow the basePath instead.";

    public override string DescribeInfo =>
        "Returns metadata about a path: exists, isDirectory, size (files only), and lastModified. "
        + "Use as a cheap guard before read/edit/move/delete to avoid errors on missing paths. "
        + "Works for files and directories; never fails on missing paths — returns exists=false instead.";

    public override string DescribeMove =>
        "Moves and/or renames a file or directory. Both arguments can be absolute paths under the "
        + "mount root, or relative paths (resolved against it). Equivalent to 'mv -T {SourcePath} "
        + "{DestinationPath}'. The destination path must not exist. Parent directories are created "
        + "automatically.";

    public override string DescribeDelete =>
        "Removes a file or directory by moving it to a trash folder. The path can be absolute "
        + "(under the mount root) or relative (resolved against it).";

    public override string DescribeCopy =>
        "Copies a file or directory within this filesystem. Both arguments can be absolute paths "
        + "under the mount root, or relative paths (resolved against it). Source must exist; if "
        + "destination exists, overwrite must be true. Parent directories are created automatically "
        + "when createDirectories=true (default).";

    public override string DescribeBlobRead =>
        "Reads a chunk of raw bytes from a file as base64. Used by the agent's cross-filesystem "
        + "transfer machinery to stream binary content. `length` is clamped to 256 KiB per call. "
        + "Returns { contentBase64, eof, totalBytes }.";

    public override string DescribeBlobWrite =>
        "Writes a chunk of raw bytes (base64-encoded) to a file at the given offset. Used by the "
        + "agent's cross-filesystem transfer machinery to stream binary content. offset=0 creates "
        + "(or, with overwrite=true, truncates) the file; later calls append at offset. "
        + "Returns { path, bytesWritten, totalBytes }.";

    public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
        _glob.Run(pattern, ct, basePath);

    public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult(_info.Run(path));

    public override Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        _move.Run(sourcePath, destinationPath, ct);

    public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        _remove.Run(path, ct);

    public override Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(_copy.Run(sourcePath, destinationPath, overwrite, createDirectories));

    // A disk root has real random access, so the ranged blob tools go straight at it rather than
    // draining the chunk stream the base default would have to.
    public override Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct) =>
        Task.FromResult(_blobRead.Run(path, offset, length));

    public override Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(_blobWrite.Run(path, contentBase64, offset, overwrite, createDirectories));

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!_blobRead.Run(path, offset, BlobReadTool.MaxChunkSizeBytes).TryGetValue(out var chunk, out var error))
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

    public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        long offset = 0;
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            write(chunk.Span, offset);
            offset += chunk.Length;
        }

        if (offset == 0)
        {
            write(ReadOnlySpan<byte>.Empty, 0);
        }

        return offset;

        void write(ReadOnlySpan<byte> bytes, long at)
        {
            var written = _blobWrite.Run(path, Convert.ToBase64String(bytes), at, overwrite, createDirectories);
            if (!written.TryGetValue(out _, out var error))
            {
                throw new FileSystemOperationException(error);
            }
        }
    }
}