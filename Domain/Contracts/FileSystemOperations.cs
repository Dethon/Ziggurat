using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;

namespace Domain.Contracts;

// One operation on a filesystem backend, named once in every vocabulary that has to talk about it:
// the backend method that implements it, the MCP tool a server advertises for it, the result type
// its payload deserialises to, and — for the ten the model can call directly — the domain tool's
// key and leaf name.
public sealed record FileSystemOperation
{
    public required string ToolName { get; init; }
    public required string MethodName { get; init; }
    public required Type ResultType { get; init; }

    // The second backend method that implements the same operation, for the streamed read alone: it
    // has a streamed shape and a ranged shape, and either one really serves the tool — the base's
    // ranged default replays the stream and honours the window, so overriding either declares the
    // capability. The write has no such pair: its ranged default cannot honour a nonzero offset (a
    // forward-only write has no position to seek to), and the transfer driver sends one per 256 KiB
    // chunk, so only the ranged override declares fs_blob_write. A backend that merely streams
    // writes advertises nothing rather than a tool that works below one chunk and fails above it.
    public string? AlternateMethodName { get; init; }

    // Null for the two byte-streaming operations: they are the agent's transfer machinery, not
    // something the model calls, so they have no domain tool and appear in no capability list.
    public string? ToolKey { get; init; }
    public string? Capability { get; init; }
}

// The one list. The registrar, the payload-type table, the capability map, the session's filter set
// and the tool feature all derive from it, so a new operation cannot half-exist because one of them
// was missed. Order is the canonical display order for a mount's capability list.
public static class FileSystemOperations
{
    // The operations a backend really implements, in this list's canonical order. An operation with
    // two shapes — the two byte-streaming ones — counts as implemented when either is overridden,
    // because either one is a working tool.
    //
    // It lives here rather than with the registrar because two callers need the same answer: the
    // registrar, deciding which fs_* tools a server advertises, and the base class, telling a model
    // what a mount can do instead of what it just refused.
    public static IReadOnlyList<FileSystemOperation> SupportedBy(Type backendType) =>
        [.. All.Where(o => Overrides(backendType, o.MethodName)
                           || (o.AlternateMethodName is { } alternate && Overrides(backendType, alternate)))];

    // The backend's own declaration of what it can do. An operation it never overrode is still the
    // base's unsupported default, so there is nothing to register. Only a true override counts:
    // a `new`-shadowing method shares the name but not the base's virtual slot, and the handlers
    // dispatch through a base-typed reference, which would land on the unsupported default.
    private static bool Overrides(Type backendType, string methodName) =>
        backendType
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Any(m => m.Name == methodName
                      && m.DeclaringType != typeof(FileSystemBackendBase)
                      && m.GetBaseDefinition().DeclaringType == typeof(FileSystemBackendBase));

    public static readonly IReadOnlyList<FileSystemOperation> All =
    [
        Op("fs_read", nameof(IFileSystemBackend.ReadAsync), typeof(FsReadResult),
            VfsFileReadTool.Key, VfsFileReadTool.Name),
        Op("fs_create", nameof(IFileSystemBackend.CreateAsync), typeof(FsCreateResult),
            VfsTextCreateTool.Key, VfsTextCreateTool.Name),
        Op("fs_edit", nameof(IFileSystemBackend.EditAsync), typeof(FsEditResult),
            VfsTextEditTool.Key, VfsTextEditTool.Name),
        Op("fs_glob", nameof(IFileSystemBackend.GlobAsync), typeof(FsGlobResult),
            VfsGlobFilesTool.Key, VfsGlobFilesTool.Name),
        Op("fs_search", nameof(IFileSystemBackend.SearchAsync), typeof(FsSearchResult),
            VfsTextSearchTool.Key, VfsTextSearchTool.Name),
        Op("fs_move", nameof(IFileSystemBackend.MoveAsync), typeof(FsMoveResult),
            VfsMoveTool.Key, VfsMoveTool.Name),
        Op("fs_copy", nameof(IFileSystemBackend.CopyAsync), typeof(FsCopyResult),
            VfsCopyTool.Key, VfsCopyTool.Name),
        Op("fs_delete", nameof(IFileSystemBackend.DeleteAsync), typeof(FsRemoveResult),
            VfsRemoveTool.Key, VfsRemoveTool.Name),
        Op("fs_info", nameof(IFileSystemBackend.InfoAsync), typeof(FsInfoResult),
            VfsFileInfoTool.Key, VfsFileInfoTool.Name),
        Op("fs_exec", nameof(IFileSystemBackend.ExecAsync), typeof(FsExecResult),
            VfsExecTool.Key, VfsExecTool.Name),
        Op("fs_blob_read", nameof(IFileSystemBackend.ReadChunksAsync), typeof(FsBlobReadResult),
            alternate: nameof(FileSystemBackendBase.ReadBlobAsync)),
        Op("fs_blob_write", nameof(FileSystemBackendBase.WriteBlobAsync), typeof(FsBlobWriteResult)),

        // Transfer machinery like the two above, so it has no tool key and no capability: the model
        // never calls it, and it appears in no mount's capability list. It is also the one operation
        // whose override means the opposite of every other's — see FileSystemBackendBase.
        Op(MoveOutCheck, nameof(IFileSystemBackend.MoveOutCheckAsync), typeof(FsMoveOutCheckResult))
    ];

    // Named once because three places have to agree on it: the list, the registrar's wiring table
    // and the proxy, which asks whether its client advertised this one tool.
    public const string MoveOutCheck = "fs_move_out_check";

    public static readonly IReadOnlySet<string> ToolNames =
        All.Select(o => o.ToolName).ToHashSet(StringComparer.Ordinal);

    private static FileSystemOperation Op(
        string toolName, string methodName, Type resultType, string? key = null, string? capability = null,
        string? alternate = null) =>
        new()
        {
            ToolName = toolName,
            MethodName = methodName,
            ResultType = resultType,
            ToolKey = key,
            Capability = capability,
            AlternateMethodName = alternate
        };
}