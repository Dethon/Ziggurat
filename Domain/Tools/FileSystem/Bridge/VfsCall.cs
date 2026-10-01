using System.Collections.Concurrent;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem.Bridge;

// One exec's capability: the session it serves, what that session's tools may do unasked, and
// the log of what the command changed. Paths arrive as the daemon sees them under /vfs — `/vault/
// notes/a.md` — which is the virtual path the file tools take, so nothing is translated twice.
public sealed class VfsCall
{
    private readonly IVirtualFileSystemRegistry _registry;
    private readonly Func<string, bool> _permits;
    private readonly IReadOnlyDictionary<string, FileSystemMount> _served;
    private readonly ConcurrentQueue<VfsChange> _changes = new();
    private int _revoked;

    internal VfsCall(
        string token,
        IVirtualFileSystemRegistry registry,
        Func<string, bool> permits,
        IReadOnlyList<FileSystemMount> served,
        DateTimeOffset expires)
    {
        Token = token;
        _registry = registry;
        _permits = permits;
        _served = served.ToDictionary(m => m.MountPoint.TrimStart('/'), StringComparer.OrdinalIgnoreCase);
        Expires = expires;
    }

    public string Token { get; }

    public DateTimeOffset Expires { get; }

    // The names the daemon puts at /vfs/<name>, the mount identity without its slash.
    public IReadOnlyList<string> ServedNames => [.. _served.Keys.Order(StringComparer.Ordinal)];

    public bool Revoked => Volatile.Read(ref _revoked) == 1;

    public IReadOnlyList<VfsChange> Changes => [.. _changes];

    internal void Revoke() => Interlocked.Exchange(ref _revoked, 1);

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
        return new BridgeAnswer<BridgeListing>.Ok(new BridgeListing(entries, value.Truncated || value.BudgetReached));
    }

    // The whole file. Bytes where the mount has them — a disk root's blob read — and otherwise the
    // text the mount renders, which is what a rendered status file is.
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
                bytes.Write(chunk.Span);
            }

            return new BridgeAnswer<byte[]>.Ok(bytes.ToArray());
        }
        catch (NotSupportedException)
        {
            return await ReadTextAsync(resolution, ct);
        }
        catch (FileSystemOperationException ex) when (ex.Error.ErrorCode == ToolError.Codes.UnsupportedOperation)
        {
            return await ReadTextAsync(resolution, ct);
        }
        catch (FileSystemOperationException ex)
        {
            return BridgeAnswer<byte[]>.From(ex.Error);
        }
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

        resolution = resolved;
        return null;
    }

    private bool Served(string path) => IsRoot(path) || _served.ContainsKey(MountName(path));

    private static bool IsRoot(string path) => path.Trim('/').Length == 0;

    private static bool IsMountRoot(string path) => !path.Trim('/').Contains('/');

    private static string MountName(string path) => path.Trim('/').Split('/')[0];

    private static string NameOf(string entry) => entry.TrimEnd('/')[(entry.TrimEnd('/').LastIndexOf('/') + 1)..];

    private static BridgeAnswer<T> NotFound<T>(string path) =>
        new BridgeAnswer<T>.Refused(Errnos.NotFound, new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.NotFound,
            Message = $"Path not found: {path}"
        });
}