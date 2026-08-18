using System.Text;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// A streamed cross-mount move is copy + delete. A backend may refuse the delete (the media
// mount's DownloadsOverlay refuses paths outside downloads/<id>) — reporting "ok" then would
// present a duplicate-leaving copy as a completed move.
public class VfsTransferDeleteSourceTests
{
    // The refusal that arrives before anything is attempted, which is what a refusal is supposed to
    // mean. The source is asked once, on the path being moved, and a no stops the transfer with
    // nothing written and nothing deleted — the old shape copied the whole file first and only then
    // discovered the source could not be removed.
    [Fact]
    public async Task MoveAsync_SourceRefusesToLetThePathLeave_StreamsNothing()
    {
        var src = new Mock<IFileSystemBackend>()
            .RefusingMoveOut("'downloads/42/a.mkv' belongs to a live download", "Wait for it to finish.")
            .HoldingFile("downloads/42/a.mkv");
        var dst = new Mock<IFileSystemBackend>();

        var result = await TransferToolDriver.MoveAsync(
            new FileSystemResolution(src.Object, "downloads/42/a.mkv"),
            new FileSystemResolution(dst.Object, "a.mkv"),
            "/media/downloads/42/a.mkv", "/vault/a.mkv",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("live download");
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        result["hint"]!.GetValue<string>().ShouldContain("Wait for it to finish.");
        dst.Verify(b => b.WriteChunksAsync(It.IsAny<string>(),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A directory move asks before the listing, not per entry: one question about the source root
    // answers for the whole subtree, and a refused move does not even enumerate.
    [Fact]
    public async Task MoveAsync_SourceRefusesToLetThePathLeave_NeverEvenLists()
    {
        var src = new Mock<IFileSystemBackend>()
            .RefusingMoveOut("'downloads/42' belongs to a live download")
            .HoldingDirectory("downloads/42");
        var dst = new Mock<IFileSystemBackend>();

        var result = await TransferToolDriver.MoveAsync(
            new FileSystemResolution(src.Object, "downloads/42"),
            new FileSystemResolution(dst.Object, "42"),
            "/media/downloads/42", "/vault/42",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["message"]!.GetValue<string>().ShouldContain("live download");
        src.Verify(b => b.GlobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A copy leaves the source in place, so there is nothing to ask about: the mount keeps its
    // files, and taking a snapshot of what a download has written so far stays possible.
    [Fact]
    public async Task CopyAsync_NeverAsksWhetherThePathMayLeave()
    {
        var src = new Mock<IFileSystemBackend>()
            .RefusingMoveOut("this would have refused a move")
            .HoldingFile("downloads/42/a.mkv")
            .HoldingDirectory("downloads/42");
        src.Setup(b => b.ReadChunksAsync("downloads/42/a.mkv", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.GlobAsync("downloads/42", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["downloads/42/a.mkv"], Truncated = false, Total = 1
            }));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var file = await TransferToolDriver.CopyAsync(
            new FileSystemResolution(src.Object, "downloads/42/a.mkv"),
            new FileSystemResolution(dst.Object, "a.mkv"),
            "/media/downloads/42/a.mkv", "/vault/a.mkv",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        var directory = await TransferToolDriver.CopyAsync(
            new FileSystemResolution(src.Object, "downloads/42"),
            new FileSystemResolution(dst.Object, "42"),
            "/media/downloads/42", "/vault/42",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        // A source that would have refused the move: both copies still complete, so neither asked.
        file["status"]!.GetValue<string>().ShouldBe("ok");
        directory["status"]!.GetValue<string>().ShouldBe("ok");
    }

    [Fact]
    public async Task MoveAsync_FileSourceDeleteRefused_ReportsTheFailureNotOk()
    {
        var src = new Mock<IFileSystemBackend>().AllowingMoveOut().HoldingFile("movies/a.mkv");
        src.Setup(b => b.ReadChunksAsync("movies/a.mkv", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.DeleteAsync("movies/a.mkv", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FsError.Fail<FsRemoveResult>(
                ToolError.Codes.UnsupportedOperation, "delete refused by the media filesystem"));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var result = await TransferToolDriver.MoveAsync(
            new FileSystemResolution(src.Object, "movies/a.mkv"),
            new FileSystemResolution(dst.Object, "a.mkv"),
            "/media/movies/a.mkv", "/vault/a.mkv",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        // Half the work stands. Repeating the move would copy what is already there, so the
        // envelope says partial rather than borrowing the delete's own refusal code — and the
        // refusal itself stays in the message, because what to do about the leftover depends on it.
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PartialSuccess);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        result["message"]!.GetValue<string>().ShouldContain("/vault/a.mkv");
        result["message"]!.GetValue<string>().ShouldContain("delete refused by the media filesystem");
        result["hint"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    // A cross-mount move of a directory with nothing to stream used to answer "ok" while doing
    // nothing: no destination was created, and the skipped delete left the source in place.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_DirectoryWithNothingToStream_ReportsTheRefusalNotOk(bool onlyDirMarkers)
    {
        string[] entries = onlyDirMarkers ? ["src/sub/"] : [];
        var src = new Mock<IFileSystemBackend>().AllowingMoveOut().HoldingDirectory("src");
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = entries, Truncated = false, Total = entries.Length
            }));
        var dst = new Mock<IFileSystemBackend>();

        var result = await TransferToolDriver.MoveAsync(
            new FileSystemResolution(src.Object, "src"),
            new FileSystemResolution(dst.Object, "dst"),
            "/media/src", "/vault/dst",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("/media/src");
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveAsync_DirectorySourceDeleteRefused_ReportsTheFailureNotOk()
    {
        var src = new Mock<IFileSystemBackend>().AllowingMoveOut().HoldingDirectory("src");
        src.Setup(b => b.GlobAsync("src", "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["src/a.md"], Truncated = false, Total = 1
            }));
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.DeleteAsync("src", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FsError.Fail<FsRemoveResult>(
                ToolError.Codes.UnsupportedOperation, "delete refused by the media filesystem"));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var result = await TransferToolDriver.MoveAsync(
            new FileSystemResolution(src.Object, "src"),
            new FileSystemResolution(dst.Object, "dst"),
            "/media/src", "/vault/dst",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PartialSuccess);
        result["message"]!.GetValue<string>().ShouldContain("/vault/dst");
        result["message"]!.GetValue<string>().ShouldContain("delete refused by the media filesystem");
        result["hint"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }
}