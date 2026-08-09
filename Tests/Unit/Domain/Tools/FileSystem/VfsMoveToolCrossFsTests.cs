using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsMoveToolCrossFsTests
{
    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsAndDeletesSource()
    {
        var src = new Mock<IFileSystemBackend>().AllowingMoveOut();
        src.SetupGet(b => b.FilesystemName).Returns("vault");
        src.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = "a.md", IsDirectory = false }));
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("hello")));
        src.Setup(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "trashed", Message = "", OriginalPath = "a.md", TrashPath = ".trash/a.md"
            }));

        var dst = new Mock<IFileSystemBackend>();
        dst.SetupGet(b => b.FilesystemName).Returns("sandbox");
        dst.Setup(b => b.WriteChunksAsync(
                "a.md", It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(Resolved(src.Object, "a.md"));
        registry.Setup(r => r.Resolve("/sandbox/a.md"))
            .Returns(Resolved(dst.Object, "a.md"));

        var tool = new VfsMoveTool(registry.Object);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        dst.Verify(b => b.WriteChunksAsync("a.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    // A native move carries no byte count, and the response used to say so with minus one — a
    // sentinel the model had to know to ignore. The field is simply absent now.
    [Fact]
    public async Task RunAsync_SameFsFile_ReportsNoByteCountRatherThanANegativeOne()
    {
        var backend = new Mock<IFileSystemBackend>();
        backend.Setup(b => b.MoveAsync("a.md", "b.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsMoveResult>.Ok(new FsMoveResult
            {
                Status = "moved", Message = "", Source = "a.md", Destination = "b.md"
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md")).Returns(Resolved(backend.Object, "a.md", "/vault"));
        registry.Setup(r => r.Resolve("/vault/b.md")).Returns(Resolved(backend.Object, "b.md", "/vault"));

        var result = await new VfsMoveTool(registry.Object).RunAsync("/vault/a.md", "/vault/b.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["source"]!.GetValue<string>().ShouldBe("/vault/a.md");
        result["destination"]!.GetValue<string>().ShouldBe("/vault/b.md");
        result["bytes"].ShouldBeNull();
    }

    // A same-mount move is the backend's own MoveAsync, which is where its refusals already live, so
    // the check is not asked at all: a backend that would refuse the path leaving the mount still
    // renames it within the mount. Asking twice would let a rule about crossing a boundary refuse a
    // move that never crosses one.
    [Fact]
    public async Task RunAsync_SameFsFile_DoesNotAskWhetherThePathMayLeave()
    {
        var backend = new Mock<IFileSystemBackend>().RefusingMoveOut("nothing may leave this mount");
        backend.Setup(b => b.MoveAsync("a.md", "b.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsMoveResult>.Ok(new FsMoveResult
            {
                Status = "moved", Message = "", Source = "a.md", Destination = "b.md"
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md")).Returns(Resolved(backend.Object, "a.md"));
        registry.Setup(r => r.Resolve("/vault/b.md")).Returns(Resolved(backend.Object, "b.md"));

        var result = await new VfsMoveTool(registry.Object).RunAsync("/vault/a.md", "/vault/b.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
    }

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));

}