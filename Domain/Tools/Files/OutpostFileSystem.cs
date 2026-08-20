using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Files;

// A machine's own filesystem, served as an ordinary mount. It is a text disk root and nothing more
// exotic: the same operations, the same virtual paths, the same refusal envelopes as the sandbox,
// which is the whole point — there is nothing new to learn per machine.
//
// The mount root is always the machine's root, jailed or not. A jail is not a different root; it
// is a refusal rule, in the same shape as the media library's — one predicate every operation asks
// before it acts. So the same file has the same name whether the outpost is jailed or not, and
// asking for something outside the working directory comes back as a refusal rather than as an
// empty result, which is how the model tells a jail from an empty directory.
public class OutpostFileSystem(
    string filesystemName,
    IFileSystemClient client,
    string workingDirectory,
    string[] allowedExtensions,
    bool jailed = false)
    : TextDiskFileSystem(
        filesystemName,
        // Placeholder: the real prose is generated below from the values this was started with,
        // and the base's stored copy is never read because DescribeMount is overridden.
        "",
        client,
        new LibraryPathConfig(MountRoot),
        allowedExtensions)
{
    public const string MountRoot = "/";

    private readonly string _workingDirectory = Rooted(workingDirectory);

    // Resolution the way the disk root beneath does it, so a relative path is judged where it will
    // actually be acted on. Containment is PathJail's — the lexical prefix rule plus the physical
    // resolution that stops a symlink inside the working directory from pointing out of it. That
    // type is named for containment against a mount root, and this is a second, narrower
    // containment on top of a mount root that has not moved; reusing the decision is what keeps a
    // jailed outpost from growing a third answer to "is this path inside that directory".
    //
    // Rooted() runs twice — a field initializer cannot read another field, and it is a pure static
    // over a string, so the second call costs a comparison at startup.
    private readonly PathJail _mount = new(MountRoot);
    private readonly PathJail _confinement = new(Rooted(workingDirectory));

    // The working directory is where files land and commands run, so it is what this mount
    // declares as its workspace — published, like every other mount's, in the backend's own
    // coordinates, which here are the machine's own absolute paths minus the leading slash.
    public override string Workspace => _workingDirectory.TrimStart('/');

    // Generated rather than written, so the prose cannot disagree with the behaviour: every fact
    // in it is read off the values the binary was started with, and there is no separate prompt
    // that could be edited into disagreeing.
    public override string DescribeMount =>
        $"{FilesystemName} — a filesystem on a real machine, mounted at {MountPoint} with the "
        + $"machine's own root as the mount root. Working directory: {VirtualWorkingDirectory} "
        + "(files land there and it is this mount's workspace). "
        + (jailed
            ? $"Jailed: every path outside {VirtualWorkingDirectory} is refused, and glob and text "
              + "search start their walk there."
            : "Not jailed: any path on the machine can be reached.")
        + (Executes
            ? $" Commands can be run on this machine with fs_exec, in {VirtualWorkingDirectory}."
            : " Commands cannot be run on this machine.")
        + " The machine decides whether this mount exists, so it can be absent for reasons that are "
        + "nobody's fault.";

    // Asks the same question the tool registrar asks — has this operation been overridden — rather
    // than carrying a flag beside the override that could drift from it.
    protected bool Executes =>
        GetType().GetMethod(nameof(ExecAsync))?.DeclaringType != typeof(FileSystemBackendBase);

    protected string WorkingDirectory => _workingDirectory;

    // Everything the model is told about this mount is in the coordinates it addresses the mount
    // in, refusals included. The machine's own spelling never reaches it.
    private string VirtualWorkingDirectory =>
        FileSystemResolution.ToVirtualPath(MountPoint, _workingDirectory);

    // The one rule. Null means the path is this outpost's business; anything else is the refusal
    // every operation hands straight back.
    private ToolErrorResult? Refuse(string path) =>
        !jailed || _confinement.Contains(_mount.Combine(path))
            ? null
            : new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.InvalidArgument,
                Message =
                    $"'{FileSystemResolution.ToVirtualPath(MountPoint, path)}' is outside this "
                    + $"outpost's working directory. {FilesystemName} is jailed to "
                    + $"{VirtualWorkingDirectory}, so it refuses every path outside it — this is a "
                    + "refusal, not an empty directory.",
                Hint = $"Work under {VirtualWorkingDirectory}."
            };

    protected FsResult<T>? Refused<T>(string path) where T : class =>
        Refuse(path) is { } error ? new FsResult<T>.Err(error) : null;

    public override async Task<FsResult<FsReadResult>> ReadAsync(
        string path, int? offset, int? limit, CancellationToken ct) =>
        Refused<FsReadResult>(path) ?? await base.ReadAsync(path, offset, limit, ct);

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Refused<FsInfoResult>(path) ?? await base.InfoAsync(path, ct);

    public override async Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Refused<FsCreateResult>(path)
        ?? await base.CreateAsync(path, content, overwrite, createDirectories, ct);

    public override async Task<FsResult<FsEditResult>> EditAsync(
        string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        Refused<FsEditResult>(path) ?? await base.EditAsync(path, edits, ct);

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        Refused<FsRemoveResult>(path) ?? await base.DeleteAsync(path, ct);

    // Both ends, because a move is two paths and either one outside the working directory is a way
    // across the boundary.
    public override async Task<FsResult<FsMoveResult>> MoveAsync(
        string sourcePath, string destinationPath, CancellationToken ct) =>
        Refused<FsMoveResult>(sourcePath)
        ?? Refused<FsMoveResult>(destinationPath)
        ?? await base.MoveAsync(sourcePath, destinationPath, ct);

    public override async Task<FsResult<FsCopyResult>> CopyAsync(
        string sourcePath, string destinationPath, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Refused<FsCopyResult>(sourcePath)
        ?? Refused<FsCopyResult>(destinationPath)
        ?? await base.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct);

    // A transfer off this mount never reaches MoveAsync — it streams the bytes out and then deletes
    // the source — so the rule is asked here too, which is what makes a transfer obey it like any
    // other operation. The override is unconditional rather than present only when jailed: this
    // mount has a rule, and whether the rule bites is a question about a path, which is the one
    // thing the capability list deliberately does not try to answer.
    public override async Task<FsResult<FsMoveOutCheckResult>> MoveOutCheckAsync(
        string path, CancellationToken ct) =>
        Refused<FsMoveOutCheckResult>(path) ?? await base.MoveOutCheckAsync(path, ct);

    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct) =>
        Refused<FsBlobReadResult>(path) ?? await base.ReadBlobAsync(path, offset, length, ct);

    public override async Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Refused<FsBlobWriteResult>(path)
        ?? await base.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct);

    // The streamed halves of the same two, asking the same rule so the halves cannot disagree. They
    // throw rather than answer an envelope because that is the shape the chunk contract has, and
    // the transfer tools turn the typed exception back into the envelope.
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (Refuse(path) is { } refusal)
        {
            throw new FileSystemOperationException(refusal);
        }

        await foreach (var chunk in base.ReadChunksAsync(path, ct).WithCancellation(ct))
        {
            yield return chunk;
        }
    }

    public override async Task<long> WriteChunksAsync(
        string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        if (Refuse(path) is { } refusal)
        {
            throw new FileSystemOperationException(refusal);
        }

        return await base.WriteChunksAsync(path, chunks, overwrite, createDirectories, ct);
    }

    // The walk starts at the working directory rather than at the machine root. Walking from / and
    // filtering afterwards would spend the 50,000-entry scan budget on directories it is going to
    // discard, and then report budgetReached for a reason the model cannot see. A basePath the
    // caller did name is refused if it points outside, like any other path argument.
    public override async Task<FsResult<FsGlobResult>> GlobAsync(
        string basePath, string pattern, CancellationToken ct) =>
        Refused<FsGlobResult>(WalkRoot(basePath))
        ?? await base.GlobAsync(WalkRoot(basePath), pattern, ct);

    public override async Task<FsResult<FsSearchResult>> SearchAsync(
        string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
        (path is not null ? Refused<FsSearchResult>(path) : null)
        ?? Refused<FsSearchResult>(WalkRoot(directoryPath))
        ?? await base.SearchAsync(
            query, regex, path, WalkRoot(directoryPath), filePattern, maxResults, contextLines,
            outputMode, ct);

    // A caller who named no scope is asking for everything this mount can see, which on a jailed
    // outpost is the working directory. Unjailed, it is the machine root, exactly as before.
    private string WalkRoot(string? basePath) =>
        jailed && string.IsNullOrEmpty(basePath?.TrimStart('/')) ? _workingDirectory : basePath ?? "";

    // A working directory is a path on somebody's machine, so it is absolute — a relative one has
    // nothing to be relative to when the binary is started from anywhere.
    //
    // The machine root is allowed, and means the whole machine: an outpost mounted at `/` with
    // `--dir /` is somebody serving their computer, which is exactly what leaving it unjailed is
    // for. It is kept as "/" rather than trimmed to the empty string so the jail and the prose have
    // a path to name. (ADR 0025's objection to a workspace that is the mount root is about landing,
    // and an outpost is never a landing target — so it does not apply here.)
    private static string Rooted(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
        {
            throw new ArgumentException(
                $"The working directory '{workingDirectory}' must be an absolute path on this machine.",
                nameof(workingDirectory));
        }

        var trimmed = workingDirectory.TrimEnd('/');
        return trimmed.Length == 0 ? MountRoot : trimmed;
    }
}