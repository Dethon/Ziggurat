using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsExecToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsExecTool _tool;

    public VfsExecToolTests()
    {
        _tool = new VfsExecTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        _registry.Setup(r => r.Resolve("/sandbox/home/sandbox_user"))
            .Returns(Resolved(_backend.Object, "home/sandbox_user"));
        _backend.Setup(b => b.ExecAsync("home/sandbox_user", "echo hi", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsExecResult>.Ok(new FsExecResult
            {
                Stdout = "hi\n", Stderr = "", ExitCode = 0, Truncated = false, TimedOut = false, DurationMs = 1, Cwd = "home/sandbox_user"
            }));

        var result = await _tool.RunAsync("/sandbox/home/sandbox_user", "echo hi");

        result!["stdout"]!.GetValue<string>().ShouldBe("hi\n");
        result["exitCode"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_PassesTimeoutSecondsThrough()
    {
        _registry.Setup(r => r.Resolve("/sandbox"))
            .Returns(Resolved(_backend.Object, ""));
        _backend.Setup(b => b.ExecAsync("", "sleep 1", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsExecResult>.Ok(new FsExecResult
            {
                Stdout = "", Stderr = "", ExitCode = 0, Truncated = false, TimedOut = false, DurationMs = 1, Cwd = ""
            }));

        var result = await _tool.RunAsync("/sandbox", "sleep 1", timeoutSeconds: 30);

        result!["exitCode"]!.GetValue<int>().ShouldBe(0);
        _backend.Verify(b => b.ExecAsync("", "sleep 1", 30, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));
}