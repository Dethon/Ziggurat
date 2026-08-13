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

    // The directory a command ran in is a path the caller never named, so it comes back in the
    // frame every other path in every other response uses: the mount point in front of the
    // backend's own root-relative answer. Feeding it into the next filesystem call is the obvious
    // next move, and it used to cost a failed one first.
    [Fact]
    public async Task RunAsync_ReportsTheWorkingDirectoryAsAVirtualPath()
    {
        _registry.Setup(r => r.Resolve("/sandbox/home/sandbox_user"))
            .Returns(Resolved(_backend.Object, "home/sandbox_user", "/sandbox"));
        _backend.Setup(b => b.ExecAsync("home/sandbox_user", "pwd", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ran("home/sandbox_user"));

        var result = await _tool.RunAsync("/sandbox/home/sandbox_user", "pwd");

        result!["cwd"]!.GetValue<string>().ShouldBe("/sandbox/home/sandbox_user");
    }

    // The container root: the backend's empty path becomes the mount point with a trailing slash,
    // which is how glob already spells a directory and what ADR-0016 says a trailing slash means.
    [Fact]
    public async Task RunAsync_AtTheContainerRoot_ReportsTheMountPointWithATrailingSlash()
    {
        _registry.Setup(r => r.Resolve("/sandbox"))
            .Returns(Resolved(_backend.Object, "", "/sandbox"));
        _backend.Setup(b => b.ExecAsync("", "pwd", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ran(""));

        var result = await _tool.RunAsync("/sandbox", "pwd");

        result!["cwd"]!.GetValue<string>().ShouldBe("/sandbox/");
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

    private static FsResult<FsExecResult> Ran(string cwd) =>
        new FsResult<FsExecResult>.Ok(new FsExecResult
        {
            Stdout = "", Stderr = "", ExitCode = 0, Truncated = false, TimedOut = false, DurationMs = 1, Cwd = cwd
        });

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));
}