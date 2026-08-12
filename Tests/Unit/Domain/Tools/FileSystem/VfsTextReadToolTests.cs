using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTextReadToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsTextReadTool _tool;

    public VfsTextReadToolTests()
    {
        _tool = new VfsTextReadTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndReturnsContent()
    {
        _registry.Setup(r => r.Resolve("/vault/notes/todo.md"))
            .Returns(Resolved(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.ReadAsync("notes/todo.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsReadResult>.Ok(new FsReadResult
            {
                FilePath = "notes/todo.md", Content = "1: hello", TotalLines = 1, Truncated = false
            }));

        var result = await _tool.RunAsync("/vault/notes/todo.md");

        result!["content"]!.GetValue<string>().ShouldBe("1: hello");
        result["totalLines"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_PassesOffsetAndLimitThrough()
    {
        _registry.Setup(r => r.Resolve("/vault/big.md"))
            .Returns(Resolved(_backend.Object, "big.md"));
        _backend.Setup(b => b.ReadAsync("big.md", 10, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsReadResult>.Ok(new FsReadResult
            {
                FilePath = "big.md", Content = "10: line", TotalLines = 100, Truncated = true
            }));

        var result = await _tool.RunAsync("/vault/big.md", offset: 10, limit: 20);

        result!["truncated"]!.GetValue<bool>().ShouldBeTrue();
        _backend.Verify(b => b.ReadAsync("big.md", 10, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_MissingFile_ReturnsTheBackendsErrorEnvelope()
    {
        _registry.Setup(r => r.Resolve("/vault/missing.md"))
            .Returns(Resolved(_backend.Object, "missing.md"));
        _backend.Setup(b => b.ReadAsync("missing.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsReadResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.NotFound, Message = "Path not found: missing.md", Retryable = false
            }));

        var result = await _tool.RunAsync("/vault/missing.md");

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
    }

    private static FsResult<FileSystemResolution> Resolved(IFileSystemBackend backend, string relativePath) =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath));
}