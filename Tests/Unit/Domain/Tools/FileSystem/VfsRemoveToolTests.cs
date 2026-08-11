using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsRemoveToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsRemoveTool _tool;

    public VfsRemoveToolTests()
    {
        _tool = new VfsRemoveTool(_registry.Object);
    }

    // The backend names the path it deleted in its own coordinates — a disk root reports the
    // container-absolute one, which the registry refuses if the model feeds it back. And the trash
    // location sits outside every mount, so no virtual path for it exists at all: the model is given
    // nothing rather than a path it cannot act on.
    [Fact]
    public async Task RunAsync_BackendLocalPath_IsReplacedWithTheCallersPathAndNoTrashLocation()
    {
        _registry.Setup(r => r.Resolve("/library/old.pdf"))
            .Returns(Resolved(_backend.Object, "old.pdf", "/library"));
        _backend.Setup(b => b.DeleteAsync("old.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "success", Message = "Moved to trash",
                OriginalPath = "/srv/library/old.pdf", TrashPath = "/srv/library/.trash/old.pdf"
            }));

        var result = await _tool.RunAsync("/library/old.pdf");

        result!["originalPath"]!.GetValue<string>().ShouldBe("/library/old.pdf");
        result["trashPath"]!.GetValue<string>().ShouldBe("");
    }

    [Fact]
    public async Task RunAsync_MissingPath_ReturnsTheBackendsErrorEnvelope()
    {
        _registry.Setup(r => r.Resolve("/library/missing.pdf"))
            .Returns(Resolved(_backend.Object, "missing.pdf"));
        _backend.Setup(b => b.DeleteAsync("missing.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsRemoveResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.NotFound, Message = "Path not found: missing.pdf", Retryable = false
            }));

        var result = await _tool.RunAsync("/library/missing.pdf");

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
    }

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));
}