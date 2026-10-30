using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;

namespace Tests.Unit.Domain.Tools.FileSystem;

// The transfer suites drive the two tools, not the machinery behind them: a registry that resolves
// the two virtual paths to the two prepared ends, then the tool the model would have called. That
// is the seam the invariant is about, and it stays the same seam when the machinery moves.
internal static class TransferToolDriver
{
    public static Task<JsonNode> CopyAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
        new VfsCopyTool(Registry(src, dst, srcVirtual, dstVirtual))
            .RunAsync(srcVirtual, dstVirtual, overwrite, createDirectories, cancellationToken: ct);

    public static Task<JsonNode> MoveAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
        new VfsMoveTool(Registry(src, dst, srcVirtual, dstVirtual))
            .RunAsync(srcVirtual, dstVirtual, overwrite, createDirectories, cancellationToken: ct);

    private static IVirtualFileSystemRegistry Registry(
        FileSystemResolution src, FileSystemResolution dst, string srcVirtual, string dstVirtual)
    {
        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(srcVirtual)).Returns(new FsResult<FileSystemResolution>.Ok(src));
        registry.Setup(r => r.Resolve(dstVirtual)).Returns(new FsResult<FileSystemResolution>.Ok(dst));
        return registry.Object;
    }
}