using System.Collections.Concurrent;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Outposts;

namespace Domain.Tools.FileSystem.Bridge;

// One exec's capability: the session it serves, what that session's tools may do unasked, and
// the log of what the command changed. Paths arrive as the daemon sees them under /vfs — `/vault/
// notes/a.md` — which is the virtual path the file tools take, so nothing is translated twice.
public sealed class VfsCall
{
    private readonly IVirtualFileSystemRegistry _registry;
    private readonly Func<string, bool> _permits;
    private readonly ConversationContext? _caller;
    private readonly IReadOnlyDictionary<string, FileSystemMount> _served;
    private readonly ConcurrentQueue<VfsChange> _changes = new();
    private readonly ConcurrentDictionary<string, byte> _truncated = new(StringComparer.Ordinal);

    // Each mount as this call's conversation sees it, made once. Whether two paths sit on one mount
    // is asked by identity (Transfer, ReplaceAsync), so a view per resolve would make every mount
    // a stranger to itself.
    private readonly ConcurrentDictionary<IFileSystemBackend, IFileSystemBackend> _views =
        new(ReferenceEqualityComparer.Instance);
    private int _revoked;

    public VfsCall(
        string token,
        IVirtualFileSystemRegistry registry,
        Func<string, bool> permits,
        IReadOnlyList<FileSystemMount> served,
        ConversationContext? caller,
        long maxFileBytes)
    {
        MaxFileBytes = maxFileBytes;
        Token = token;
        _registry = registry;
        _permits = permits;
        _caller = caller;
        _served = served.ToDictionary(m => m.MountPoint.TrimStart('/'), StringComparer.OrdinalIgnoreCase);
    }

    public string Token { get; }

    // The largest file this call reads or writes through a mount (VfsBridgeSettings).
    public long MaxFileBytes { get; }

    // The names the daemon puts at /vfs/<name>, the mount identity without its slash.
    public IReadOnlyList<string> ServedNames => [.. _served.Keys.Order(StringComparer.Ordinal)];

    public bool Revoked => Volatile.Read(ref _revoked) == 1;

    // The one rule for which mounts a command sees: every mount but an outpost — a separate
    // machine, never reachable from the sandbox — and a mount with a shell of its own, the
    // sandbox being the command's own disk already. The prompt, the screen's reach and the exec
    // tool's rerouting all ask this.
    public static bool IsServed(FileSystemMount mount) =>
        !OutpostMountPoint.Addresses(mount.MountPoint) && mount.ShellReach is null;

    public IReadOnlyList<VfsChange> Changes => [.. _changes];

    // Directories whose listing the mount's walk budget cut short.
    public IReadOnlyList<string> Truncated => [.. _truncated.Keys.Order(StringComparer.Ordinal)];

    public void Revoke() => Interlocked.Exchange(ref _revoked, 1);

    internal void Record(VfsChange change) => _changes.Enqueue(change);

    public async Task<BridgeAnswer<BridgeAttr>> AttrAsync(string path, CancellationToken ct)
    {
        if (IsRoot(path) || IsMountRoot(path))
        {
            return Served(path) ? new BridgeAnswer<BridgeAttr>.Ok(new BridgeAttr(BridgeKinds.Directory, null)) : NotFound<BridgeAttr>(path);
        }

        if (Resolve<BridgeAttr>(path, VfsFileInfoTool.Name, out var resolution) is { } refused)
        {
            return refused;
        }

        var info = await resolution.Backend.InfoAsync(resolution.RelativePath, ct);
        if (!info.TryGetValue(out var value, out var error))
        {
            return error.ErrorCode == ToolError.Codes.UnsupportedOperation
                ? await AttrFromParentAsync(path, ct)
                : BridgeAnswer<BridgeAttr>.From(error);
        }

        return !value.Exists
            ? NotFound<BridgeAttr>(path)
            : new BridgeAnswer<BridgeAttr>.Ok(value switch
            {
                { IsDirectory: true } => new BridgeAttr(BridgeKinds.Directory, null),
                { Executable: true } => new BridgeAttr(BridgeKinds.Action, null),
                _ => new BridgeAttr(BridgeKinds.File, value.Size)
            });
    }

    // A mount with no info answers by its listing, where the entry's kind is all there is to know.
    private async Task<BridgeAnswer<BridgeAttr>> AttrFromParentAsync(string path, CancellationToken ct)
    {
        var name = path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];
        var parent = path.TrimEnd('/')[..path.TrimEnd('/').LastIndexOf('/')];
        return await ListAsync(parent.Length == 0 ? "/" : parent, ct) switch
        {
            BridgeAnswer<BridgeListing>.Ok ok when ok.Value.Entries.FirstOrDefault(e => e.Name == name) is { } entry =>
                new BridgeAnswer<BridgeAttr>.Ok(new BridgeAttr(entry.Kind, null)),
            BridgeAnswer<BridgeListing>.Refused refused => new BridgeAnswer<BridgeAttr>.Refused(refused.Errno, refused.Error),
            _ => NotFound<BridgeAttr>(path)
        };
    }

    public async Task<BridgeAnswer<BridgeListing>> ListAsync(string path, CancellationToken ct)
    {
        if (IsRoot(path))
        {
            return new BridgeAnswer<BridgeListing>.Ok(new BridgeListing(
                [.. ServedNames.Select(n => new BridgeEntry(n, BridgeKinds.Directory))], false));
        }

        if (Resolve<BridgeListing>(path, VfsGlobFilesTool.Name, out var resolution) is { } refused)
        {
            return refused;
        }

        var glob = await resolution.Backend.GlobAsync(resolution.RelativePath, "*", ct);
        if (!glob.TryGetValue(out var value, out var error))
        {
            return BridgeAnswer<BridgeListing>.From(error);
        }

        var executables = (value.Executables ?? []).Select(NameOf).ToHashSet(StringComparer.Ordinal);
        var entries = value.Entries
            .Select(e => new BridgeEntry(
                NameOf(e),
                e.EndsWith('/') ? BridgeKinds.Directory
                : executables.Contains(NameOf(e)) ? BridgeKinds.Action
                : BridgeKinds.File))
            .Where(e => e.Name.Length > 0)
            .DistinctBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var truncated = value.Truncated || value.BudgetReached;
        if (truncated)
        {
            _truncated.TryAdd(path.TrimEnd('/'), 0);
        }

        return new BridgeAnswer<BridgeListing>.Ok(new BridgeListing(entries, truncated));
    }

    // The whole file, up to the call's ceiling. Bytes where the mount has them — a disk root's blob
    // read — and otherwise the text the mount renders, which is what a rendered status file is. A
    // file past the ceiling is refused the moment the read passes it: what was read so far is let
    // go and the rest is never asked for.
    public async Task<BridgeAnswer<byte[]>> ReadAsync(string path, CancellationToken ct)
    {
        if (Resolve<byte[]>(path, VfsFileReadTool.Name, out var resolution) is { } refused)
        {
            return refused;
        }

        try
        {
            using var bytes = new MemoryStream();
            await foreach (var chunk in resolution.Backend.ReadChunksAsync(resolution.RelativePath, ct))
            {
                if (bytes.Length + chunk.Length > MaxFileBytes)
                {
                    return TooLarge<byte[]>(path);
                }

                bytes.Write(chunk.Span);
            }

            return new BridgeAnswer<byte[]>.Ok(bytes.ToArray());
        }
        catch (NotSupportedException)
        {
            return Within(path, await ReadTextAsync(resolution, ct));
        }
        catch (FileSystemOperationException ex) when (ex.Error.ErrorCode == ToolError.Codes.UnsupportedOperation)
        {
            return Within(path, await ReadTextAsync(resolution, ct));
        }
        catch (FileSystemOperationException ex)
        {
            return BridgeAnswer<byte[]>.From(ex.Error);
        }
    }

    // A file the command wrote, whole, as the equivalent tool call would write it: text as a create
    // with overwrite (the text tool, which applies the mount's own rules about what it authors as
    // text), anything else as the blob write a copy streams through, where the mount offers one.
    // `isNew` is the daemon's knowledge that nothing was at the path, which only names the change.
    public async Task<BridgeAnswer<bool>> WriteAsync(string path, byte[] content, bool isNew, CancellationToken ct)
    {
        if (content.Length > MaxFileBytes)
        {
            return WriteTooLarge(path, isNew);
        }

        var operation = isNew ? VfsChange.Operations.Create : VfsChange.Operations.Write;
        return Revoked
            ? Dropped<bool>(path, operation)
            : Logged(await WriteCoreAsync(path, content, ct), path, operation);
    }

    // A write whose content passed the ceiling. Its own call because the endpoint stops reading a
    // body there: it has no content to hand over, only a refusal for the change log.
    public BridgeAnswer<bool> WriteTooLarge(string path, bool isNew)
    {
        var operation = isNew ? VfsChange.Operations.Create : VfsChange.Operations.Write;
        return Revoked ? Dropped<bool>(path, operation) : Logged(TooLarge<bool>(path), path, operation);
    }

    // The remove tool's call. A directory goes whole: `rm -r` reaches here as the directory's one
    // delete, which on a timer is its cancel.
    public async Task<BridgeAnswer<bool>> DeleteAsync(string path, CancellationToken ct) =>
        Revoked
            ? Dropped<bool>(path, VfsChange.Operations.Delete)
            : Logged(await DeleteCoreAsync(path, ct), path, VfsChange.Operations.Delete);

    // One rename from the kernel, within a mount or across two (both sit in one FUSE filesystem
    // behind links, so `mv` between mounts arrives here whole). Onto something already there it is
    // judged as a write to that path — the spike's `sed -i` replaced a refusing file exactly this
    // way — and otherwise it is the move tool's call: the mount's own move within one mount, a
    // transfer with move intent across two, so the source is asked whether the path may leave
    // before anything is copied.
    public async Task<BridgeAnswer<bool>> RenameAsync(string from, string to, bool overwrite, CancellationToken ct)
    {
        if (Revoked)
        {
            return Dropped<bool>(from, VfsChange.Operations.Move, to);
        }

        if (Resolve<bool>(from, VfsMoveTool.Name, out var source) is { } refusedSource)
        {
            return Logged(refusedSource, from, VfsChange.Operations.Move, to);
        }

        if (Resolve<bool>(to, VfsMoveTool.Name, out var destination) is { } refusedDestination)
        {
            return Logged(refusedDestination, from, VfsChange.Operations.Move, to);
        }

        var answer = overwrite
            ? await ReplaceAsync(source, destination, from, to, ct)
            : (await Transfer.RunAsync(new TransferRequest
            {
                Source = source,
                Destination = destination,
                SourcePath = from,
                DestinationPath = to,
                Intent = TransferIntent.Move
            }, ct)).TryGetValue(out _, out var error)
                ? new BridgeAnswer<bool>.Ok(true)
                : BridgeAnswer<bool>.From(error);
        return Logged(answer, from, VfsChange.Operations.Move, to);
    }

    // An action file run from a script: the exec tool's call on the action's directory, `./<name>`
    // with the script's arguments as the words they were, so the mount's own catalog decides what it
    // means. Every held file was committed by the daemon before this arrived, so an action sees what
    // the script prepared.
    public async Task<BridgeAnswer<BridgeActionResult>> ActionAsync(string path, IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (Revoked)
        {
            return Dropped<BridgeActionResult>(path, VfsChange.Operations.Action);
        }

        var directory = path.TrimEnd('/')[..path.TrimEnd('/').LastIndexOf('/')];
        if (Resolve<BridgeActionResult>(directory, VfsExecTool.Name, out var resolution) is { } refused)
        {
            return Logged(refused, path, VfsChange.Operations.Action);
        }

        var command = string.Join(' ', argv.Select(ShellQuote).Prepend($"./{path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..]}"));
        var answer = (await resolution.Backend.ExecAsync(resolution.RelativePath, command, null, ct)).TryGetValue(out var exec, out var error)
            ? new BridgeAnswer<BridgeActionResult>.Ok(new BridgeActionResult(exec.Stdout, exec.Stderr, exec.ExitCode))
            : BridgeAnswer<BridgeActionResult>.From(error);
        return Logged(answer, path, VfsChange.Operations.Action);
    }

    // The quoting the mounts' tokenizer reads back as one word.
    private static string ShellQuote(string word) =>
        word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,@%+".Contains(c))
            ? word
            : $"'{word.Replace("'", "'\\''")}'";

    // The target takes the source's content under a write's rules, and only then does the source go.
    // Across mounts the source is first asked whether the path may leave it (ADR 0015), as the
    // transfer a move onto a new path makes would ask it — a refusal keeps both ends as they were.
    private async Task<BridgeAnswer<bool>> ReplaceAsync(
        FileSystemResolution source, FileSystemResolution destination, string from, string to, CancellationToken ct)
    {
        if (!ReferenceEquals(source.Backend, destination.Backend)
            && !(await source.Backend.MoveOutCheckAsync(source.RelativePath, ct)).TryGetValue(out _, out var refusal))
        {
            return BridgeAnswer<bool>.From(refusal);
        }

        // The source goes last, so whether a command may remove it is settled before the target is
        // touched: the gate's answer does not change in between.
        if (Resolve<bool>(from, VfsRemoveTool.Name, out _) is { } unremovable)
        {
            return unremovable;
        }

        var read = await ReadAsync(from, ct);
        if (read is not BridgeAnswer<byte[]>.Ok content)
        {
            var unread = (BridgeAnswer<byte[]>.Refused)read;
            return new BridgeAnswer<bool>.Refused(unread.Errno, unread.Error);
        }

        var written = await WriteCoreAsync(to, content.Value, ct);
        if (written is not BridgeAnswer<bool>.Ok)
        {
            return written;
        }

        // What the gate cannot know is the mount's own answer: a source it keeps is found out only
        // now, with the target already written. The move is refused, and the write that landed is
        // listed as the change it was — the list is the one record of what a command really did.
        var removed = await DeleteCoreAsync(from, ct);
        if (removed is BridgeAnswer<bool>.Refused)
        {
            Logged(written, to, VfsChange.Operations.Write);
        }

        return removed;
    }

    private async Task<BridgeAnswer<bool>> DeleteCoreAsync(string path, CancellationToken ct)
    {
        if (Resolve<bool>(path, VfsRemoveTool.Name, out var resolution) is { } refused)
        {
            return refused;
        }

        return (await resolution.Backend.DeleteAsync(resolution.RelativePath, ct)).TryGetValue(out _, out var error)
            ? new BridgeAnswer<bool>.Ok(true)
            : BridgeAnswer<bool>.From(error);
    }

    private async Task<BridgeAnswer<bool>> WriteCoreAsync(string path, byte[] content, CancellationToken ct)
    {
        var text = AsText(content);
        if (Resolve<bool>(path, text is null ? VfsCopyTool.Name : VfsTextCreateTool.Name, out var resolution) is { } refused)
        {
            return refused;
        }

        return text is not null
            ? (await resolution.Backend.CreateAsync(resolution.RelativePath, text, overwrite: true, createDirectories: true, ct))
                .TryGetValue(out _, out var error)
                ? new BridgeAnswer<bool>.Ok(true)
                : BridgeAnswer<bool>.From(error)
            : await WriteBytesAsync(resolution, content, ct);
    }

    private static async Task<BridgeAnswer<bool>> WriteBytesAsync(FileSystemResolution resolution, byte[] content, CancellationToken ct)
    {
        try
        {
            await resolution.Backend.WriteChunksAsync(
                resolution.RelativePath, One(content), overwrite: true, createDirectories: true, ct);
            return new BridgeAnswer<bool>.Ok(true);
        }
        catch (NotSupportedException ex)
        {
            return BridgeAnswer<bool>.From(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.UnsupportedOperation,
                Message = $"{ex.Message} Only text can be written here."
            });
        }
        catch (FileSystemOperationException ex)
        {
            return BridgeAnswer<bool>.From(ex.Error);
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> One(byte[] content)
    {
        await Task.CompletedTask;
        yield return content;
    }

    // Text is what decodes as UTF-8 and holds no NUL: what a person would call a text file, and
    // what the text tool can carry as a string.
    private static string? AsText(byte[] content)
    {
        if (content.Contains((byte)0))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // Every change the command made through the mounts is recorded with the bridge's verdict,
    // because bash swallows a refusal at close and this log is all the agent will see of it.
    private BridgeAnswer<T> Logged<T>(BridgeAnswer<T> answer, string path, string operation, string? destination = null)
    {
        Record(new VfsChange
        {
            Path = path,
            Operation = operation,
            Destination = destination,
            Status = answer is BridgeAnswer<T>.Ok ? VfsChange.Statuses.Applied : VfsChange.Statuses.Refused,
            Error = (answer as BridgeAnswer<T>.Refused)?.Error?.ToNode()
        });
        return answer;
    }

    // Arrived after the command was killed or the call cancelled: never applied, and said so.
    private BridgeAnswer<T> Dropped<T>(string path, string operation, string? destination = null)
    {
        Record(new VfsChange
        {
            Path = path,
            Operation = operation,
            Destination = destination,
            Status = VfsChange.Statuses.Dropped
        });
        return new BridgeAnswer<T>.Refused(Errnos.Denied, null);
    }

    private static async Task<BridgeAnswer<byte[]>> ReadTextAsync(FileSystemResolution resolution, CancellationToken ct) =>
        (await resolution.Backend.ReadAsync(resolution.RelativePath, null, null, ct)).TryGetValue(out var text, out var error)
            ? new BridgeAnswer<byte[]>.Ok(Encoding.UTF8.GetBytes(text.Content))
            : BridgeAnswer<byte[]>.From(error);

    // The one gate every operation passes: the path names a mount this call serves, and the tool
    // the operation stands for would run unasked. A call that would put the question to a person is
    // refused instead — a syscall never waits on anyone.
    private BridgeAnswer<T>? Resolve<T>(string path, string toolName, out FileSystemResolution resolution)
    {
        resolution = null!;
        if (!Served(path))
        {
            return NotFound<T>(path);
        }

        if (!_permits(toolName))
        {
            return new BridgeAnswer<T>.Refused(Errnos.Denied, new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.PermissionDenied,
                Message = $"{FileSystemToolFeature.Callable(toolName)} would ask the person before it ran, "
                          + "so a command cannot do it unasked."
            });
        }

        if (!_registry.Resolve(path).TryGetValue(out var resolved, out var error))
        {
            return BridgeAnswer<T>.From(error);
        }

        // The backend as this conversation sees it: the operation runs on the bridge's own request,
        // where no turn is in flight to read the caller from.
        resolution = resolved.Backend is ICallerBoundBackend bound
            ? resolved with { Backend = _views.GetOrAdd(resolved.Backend, _ => bound.As(_caller)) }
            : resolved;
        return null;
    }

    private bool Served(string path) => IsRoot(path) || _served.ContainsKey(MountName(path));

    private static bool IsRoot(string path) => path.Trim('/').Length == 0;

    private static bool IsMountRoot(string path) => !path.Trim('/').Contains('/');

    private static string MountName(string path) => path.Trim('/').Split('/')[0];

    private static string NameOf(string entry) => entry.TrimEnd('/')[(entry.TrimEnd('/').LastIndexOf('/') + 1)..];

    private BridgeAnswer<byte[]> Within(string path, BridgeAnswer<byte[]> answer) =>
        answer is BridgeAnswer<byte[]>.Ok { Value.LongLength: var length } && length > MaxFileBytes
            ? TooLarge<byte[]>(path)
            : answer;

    private BridgeAnswer<T> TooLarge<T>(string path) =>
        new BridgeAnswer<T>.Refused(Errnos.TooLarge, new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.InvalidArgument,
            Message = $"{path} is larger than {MaxFileBytes} bytes, the most a command can read or write "
                      + "through a mount. The file tools are not bound by this."
        });

    private static BridgeAnswer<T> NotFound<T>(string path) =>
        new BridgeAnswer<T>.Refused(Errnos.NotFound, new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.NotFound,
            Message = $"Path not found: {path}"
        });
}