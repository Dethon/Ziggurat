using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;
using Tests.Unit.Domain.Tools.FileSystem;
using static Tests.Unit.Domain.Downloads.Vfs.DownloadFakes;

namespace Tests.Unit.Domain.Downloads.Vfs;

// The media mount is the disk root plus the downloads overlay, and every hole in that seam has the
// same shape: an operation the overlay never saw reaches the disk underneath and writes, moves or
// lists something the overlay claims to own.
public class MediaLibraryFileSystemTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly FakeDownloadClient _client;
    private readonly RecordingFileSystemClient _disk;
    private readonly MediaLibraryDiskFileSystem _sut;

    public MediaLibraryFileSystemTests()
    {
        _libraryRoot = Path.Combine(Path.GetTempPath(), $"media-fs-{Guid.NewGuid()}");
        Directory.CreateDirectory(_libraryRoot);
        var overlay = BuildOverlay(_libraryRoot, out _client, out _, out _disk);
        _sut = new MediaLibraryDiskFileSystem(_disk, new LibraryPathConfig(_libraryRoot), overlay);
    }

    public void Dispose()
    {
        if (Directory.Exists(_libraryRoot))
        {
            Directory.Delete(_libraryRoot, true);
        }

        GC.SuppressFinalize(this);
    }

    // A leftover status file is an ordinary file, so both halves of a write reach the disk.
    [Fact]
    public async Task WritesOntoALeftoverStatusFile_Succeed()
    {
        await WriteLeftoverStatus();

        (await _sut.WriteBlobAsync("downloads/99/status.json", Convert.ToBase64String("ranged"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: false, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();

        (await _sut.WriteChunksAsync("downloads/99/status.json", Chunks("streamed"),
            overwrite: true, createDirectories: false, CancellationToken.None)).ShouldBe(8);

        (await File.ReadAllTextAsync(Path.Combine(_libraryRoot, "downloads", "99", "status.json")))
            .ShouldBe("streamed");
    }

    // Both halves of a read of a live download's status file answer the same thing: it is a
    // rendered view, so read it as text. The streamed half says so through the typed filesystem
    // exception, which is the only shape its signature has.
    [Fact]
    public async Task ReadsOfALiveDownloadsStatusFile_AreRefusedTheSameWay()
    {
        _client.Add(Item(42));

        var ranged = (await _sut.ReadBlobAsync("downloads/42/status.json", 0, 10, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Err>().Error;
        ranged.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        ranged.Message.ShouldContain("fs_read");

        var streamed = await Should.ThrowAsync<FileSystemOperationException>(
            () => Collect("downloads/42/status.json"));
        streamed.Error.ErrorCode.ShouldBe(ranged.ErrorCode);
        streamed.Error.Message.ShouldBe(ranged.Message);
    }

    // A status.json no live download owns is a leftover: an ordinary file the disk owns. A ranged
    // read already served it while the streamed read refused it as a virtual file, so the same file
    // read differently depending on which side of the MCP seam the caller sat on.
    [Fact]
    public async Task ReadsOfALeftoverStatusFile_BothServeTheRealFile()
    {
        await WriteLeftoverStatus();

        var ranged = (await _sut.ReadBlobAsync("downloads/99/status.json", 0, 1024, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Ok>().Value;

        Encoding.UTF8.GetString(Convert.FromBase64String(ranged.ContentBase64)).ShouldContain("stale");
        (await Collect("downloads/99/status.json")).ShouldContain("stale");
    }

    // Only the status file of a live download is a rendered view. The payload files that download
    // is writing are plain disk entries, so reading their bytes while it runs is allowed.
    [Fact]
    public async Task ReadsOfAPayloadInsideALiveDownload_BothServeTheRealFile()
    {
        _client.Add(Item(42));
        var payload = Path.Combine(_libraryRoot, "downloads", "42", "book.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        await File.WriteAllTextAsync(payload, "book");

        (await _sut.ReadBlobAsync("downloads/42/book.epub", 0, 1024, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Ok>();
        (await Collect("downloads/42/book.epub")).ShouldBe("book");
    }

    // A leftover download directory owns nothing, so writing into it is an ordinary write.
    [Fact]
    public async Task WritesIntoALeftoverDownloadDirectory_Succeed()
    {
        _client.Add(Item(42));
        Directory.CreateDirectory(Path.Combine(_libraryRoot, "downloads", "99"));

        (await _sut.WriteBlobAsync("downloads/99/book.epub", Convert.ToBase64String("book"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: false, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();

        (await _sut.WriteChunksAsync("downloads/99/other.epub", Chunks("book"),
            overwrite: true, createDirectories: false, CancellationToken.None)).ShouldBe(4);
    }

    // The disk resolves '.' and '..' before it writes, so a dotted spelling of the status path lands
    // on exactly the file the overlay shadows. The classifier used to read the caller's literal
    // spelling, so every refusal on this mount was one '.' away from being switched off.
    [Theory]
    [InlineData("downloads/42/./status.json")]
    [InlineData("downloads/43/../42/status.json")]
    public async Task WriteChunks_OntoADottedSpellingOfTheVirtualStatusFile_IsRefused(string path)
    {
        _client.Add(Item(42));

        var write = await Should.ThrowAsync<FileSystemOperationException>(() => _sut.WriteChunksAsync(
            path, Chunks("stale snapshot"), overwrite: true, createDirectories: true, CancellationToken.None));

        write.Error.Message.ShouldContain("removed when the download is cancelled");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "status.json")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("downloads/42/./status.json")]
    [InlineData("downloads/43/../42/status.json")]
    public async Task BlobWrite_OntoADottedSpellingOfTheVirtualStatusFile_IsRefused(string path)
    {
        _client.Add(Item(42));

        var write = await _sut.WriteBlobAsync(path, Convert.ToBase64String("stale"u8.ToArray()),
            offset: 0, overwrite: true, createDirectories: true, CancellationToken.None);

        var error = write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("removed when the download is cancelled");
        Directory.Exists(Path.Combine(_libraryRoot, "downloads", "42")).ShouldBeFalse();
    }

    // A directory whose name merely looks like an id is an ordinary directory, so a write into it
    // is an ordinary write.
    [Theory]
    [InlineData("042")]
    public async Task BlobWrite_IntoALookalikeDownloadDirectory_Succeeds(string dir)
    {
        _client.Add(Item(42));

        (await _sut.WriteBlobAsync($"downloads/{dir}/book.epub", Convert.ToBase64String("book"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();
    }

    [Fact]
    public async Task BlobWrite_IntoAnAbsoluteSpellingOfALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));

        var write = await _sut.WriteBlobAsync(
            Path.Combine(_libraryRoot, "downloads", "42", "book.epub"),
            Convert.ToBase64String("book"u8.ToArray()),
            offset: 0, overwrite: true, createDirectories: true, CancellationToken.None);

        write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>().Error.Message.ShouldContain("removed when the download is cancelled");
    }

    // A read intent classifies one path, whichever way that path is spelled: the dotted and
    // absolute forms resolve to the same file, and a directory whose name merely looks like an id
    // is an ordinary directory no download owns.
    [Theory]
    [InlineData("downloads/42/./status.json")]
    [InlineData("downloads/43/../42/status.json")]
    public async Task StreamedReadOfALiveStatusFile_IsRefusedWhicheverWayItIsSpelled(string path)
    {
        _client.Add(Item(42));

        var streamed = await Should.ThrowAsync<FileSystemOperationException>(() => Collect(path));

        streamed.Error.Message.ShouldContain("fs_read");
    }

    [Fact]
    public async Task StreamedReadOfAnAbsoluteSpellingOfALiveStatusFile_IsRefused()
    {
        _client.Add(Item(42));

        var streamed = await Should.ThrowAsync<FileSystemOperationException>(
            () => Collect(Path.Combine(_libraryRoot, "downloads", "42", "status.json")));

        streamed.Error.Message.ShouldContain("fs_read");
    }

    // 'downloads/042' is a real directory on disk, not download 42, so a status.json under it is an
    // ordinary file both reads serve.
    [Theory]
    [InlineData("042")]
    public async Task ReadsOfAStatusFileUnderALookalikeId_ServeTheRealFile(string dir)
    {
        _client.Add(Item(42));
        var file = Path.Combine(_libraryRoot, "downloads", dir, "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{\"real\":true}");

        (await _sut.ReadBlobAsync($"downloads/{dir}/status.json", 0, 1024, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Ok>();
        (await Collect($"downloads/{dir}/status.json")).ShouldContain("real");
    }

    // The only text this mount reads is a status file; everything else on it is bytes. That is true
    // of a leftover status file too — it reads as the ordinary file it is.
    [Theory]
    [InlineData("downloads/42")]
    [InlineData("downloads/42/payload.mkv")]
    [InlineData("downloads/99")]
    [InlineData("Movies/film.mkv")]
    public async Task TextRead_OfAPathThatIsNotAStatusFile_IsRefused(string path)
    {
        _client.Add(Item(42));

        var error = (await _sut.ReadAsync(path, null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("status.json");
    }

    [Fact]
    public async Task TextRead_OfALiveDownloadsStatusFile_ReportsItsState()
    {
        _client.Add(Item(42));

        var read = (await _sut.ReadAsync("downloads/42/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("etaMinutes");
    }

    [Fact]
    public async Task WriteChunks_OntoAPlainMediaPath_StillWrites()
    {
        var written = await _sut.WriteChunksAsync(
            "Movies/notes.txt", Chunks("hello"),
            overwrite: true, createDirectories: true, CancellationToken.None);

        written.ShouldBe(5);
        (await File.ReadAllTextAsync(Path.Combine(_libraryRoot, "Movies", "notes.txt"))).ShouldBe("hello");
    }

    // A path with nothing to do with downloads is the disk's for both byte operations.
    [Fact]
    public async Task BlobReadAndBlobWrite_OfAPlainMediaPath_StillReachTheDisk()
    {
        _client.Add(Item(42));

        (await _sut.WriteBlobAsync("Movies/notes.txt", Convert.ToBase64String("hello"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();

        var blob = (await _sut.ReadBlobAsync("Movies/notes.txt", 0, 1024, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobReadResult>.Ok>().Value;

        Encoding.UTF8.GetString(Convert.FromBase64String(blob.ContentBase64)).ShouldBe("hello");
    }

    // Deleting downloads/<id> is the documented cancel, but moving it is not: qBittorrent keeps
    // writing, recreates the directory it lost, and a later delete then cancels and cleans the
    // recreated one while the moved copy is orphaned. The refusal covers the download directory and
    // anything above it, because moving the parent takes the same directory with it.
    [Theory]
    [InlineData("downloads/42")]
    [InlineData("downloads")]
    [InlineData("/downloads/42")]
    [InlineData("downloads/./42")]
    [InlineData("downloads/43/../42")]
    public async Task Move_APathHoldingALiveDownload_IsRefused(string source)
    {
        _client.Add(Item(42));

        var move = await _sut.MoveAsync(source, "Movies/42", CancellationToken.None);

        var error = move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("has not finished");
    }

    // The other half of the same boundary. A payload file inside a live download is not "above" the
    // download directory, so the ancestor rule never saw it: moving it out left qBittorrent
    // rewriting the file it still owns and the moved copy orphaned. And a move whose destination
    // lands inside the directory puts the file where delete-as-cancel destroys it. Each end gets
    // the reason belonging to that end, so the agent is told which one offended.
    [Theory]
    [InlineData("downloads/42/payload.mkv", "Movies/payload.mkv", "moving across that boundary")]
    [InlineData("downloads/42/status.json", "Movies/status.json", "moving across that boundary")]
    [InlineData("Movies/payload.mkv", "downloads/42/payload.mkv", "is removed when the download is cancelled")]
    [InlineData("Movies/payload.mkv", "downloads/42", "is removed when the download is cancelled")]
    [InlineData("Movies/payload.mkv", "downloads", "is removed when the download is cancelled")]
    public async Task Move_AcrossALiveDownloadsBoundary_IsRefused(string source, string destination, string reason)
    {
        _client.Add(Item(42));

        var move = await _sut.MoveAsync(source, destination, CancellationToken.None);

        var error = move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain(reason);
    }

    // Both ends offend; the source is the one named, because that is the end the agent has to stop
    // asking about.
    [Fact]
    public async Task Move_BetweenTwoLiveDownloads_NamesTheSource()
    {
        _client.Add(Item(42));
        _client.Add(Item(43));

        var move = await _sut.MoveAsync("downloads/42/payload.mkv", "downloads/43/payload.mkv",
            CancellationToken.None);

        var error = move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error;
        error.Message.ShouldContain("downloads/42/payload.mkv");
        error.Message.ShouldContain("moving across that boundary");
    }

    // Organising a finished download into the library is exactly what the completion alert asks the
    // agent to do, and qBittorrent keeps the torrent listed while it seeds — so "the client still
    // lists it" cannot be what refuses the move. Only a download that is still writing can.
    [Theory]
    [InlineData("downloads/42/payload.mkv", "Movies/payload.mkv")]
    [InlineData("downloads/42", "Movies/Show")]
    public async Task Move_OfAFinishedDownloadsFiles_StillMoves(string source, string destination)
    {
        _client.Add(Item(42, DownloadState.Completed));

        (await _sut.MoveAsync(source, destination, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
    }

    // Finished is the only state that frees the files. A paused download resumes into them and a
    // failed one is a partial file the agent should cancel rather than file away.
    [Theory]
    [InlineData(DownloadState.Paused)]
    [InlineData(DownloadState.Failed)]
    public async Task Move_OfADownloadThatHasNotFinished_IsRefused(DownloadState state)
    {
        _client.Add(Item(42, state));

        var error = (await _sut.MoveAsync("downloads/42/payload.mkv", "Movies/payload.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("has not finished");
    }

    // The way out opens when the download finishes; the way in does not. Delete-as-cancel removes
    // the directory whole for as long as the client owns the id, so anything landing inside it still
    // dies with the download.
    [Fact]
    public async Task Move_IntoAFinishedDownloadsDirectory_IsStillRefused()
    {
        _client.Add(Item(42, DownloadState.Completed));

        (await _sut.MoveAsync("Movies/payload.mkv", "downloads/42/payload.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>()
            .Error.Message.ShouldContain("removed when the download is cancelled");
    }

    // A finished download's status file is still rendered for as long as the client lists the
    // torrent, so there are no bytes on disk to move. Letting the move through would answer
    // not_found for a path fs_info, fs_glob and fs_read all report as existing.
    [Fact]
    public async Task Move_OfAFinishedDownloadsStatusFile_IsStillRefused()
    {
        _client.Add(Item(42, DownloadState.Completed));

        var error = (await _sut.MoveAsync("downloads/42/status.json", "Movies/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("rendered");
    }

    [Fact]
    public async Task Move_APathWithNoLiveDownloadUnderIt_StillMoves()
    {
        _client.Add(Item(42));

        (await _sut.MoveAsync("Movies/old", "Movies/new", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
        (await _sut.MoveAsync("downloads/7", "Movies/7", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
    }

    // A leftover is an ordinary file wherever it sits, so moving one is an ordinary move.
    [Theory]
    [InlineData("downloads/99")]
    [InlineData("downloads/99/status.json")]
    public async Task Move_ALeftover_StillMoves(string source)
    {
        _client.Add(Item(42));
        await WriteLeftoverStatus();

        (await _sut.MoveAsync(source, "Movies/archived", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
    }

    // The move-out check is the same rule a same-mount move asks about its source end, reachable
    // from the side of the seam the agent is on. One question about one path, so it answers for
    // the whole subtree: an ancestor of a live download's directory and anything inside one both
    // offend.
    [Theory]
    [InlineData("downloads/42")]
    [InlineData("downloads/42/payload.mkv")]
    [InlineData("downloads/42/status.json")]
    [InlineData("downloads")]
    public async Task MoveOutCheck_OfAPathALiveDownloadOwns_IsRefusedWithTheMoveOutReason(string path)
    {
        _client.Add(Item(42));

        var error = (await _sut.MoveOutCheckAsync(path, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain(path);
        error.Message.ShouldContain("moving across that boundary");
        error.Retryable.ShouldBeFalse();
    }

    // A cross-mount move is a streamed copy and then a delete of the source, so the check has to
    // answer for both halves. fs_delete on this mount removes nothing but download directories and
    // leftovers, so an ordinary media path could never finish the move: it used to stream every
    // byte to the other mount and only then discover the source could not be removed, leaving a
    // duplicate on both mounts. The reason is the delete's own, because the delete is what refuses.
    [Theory]
    [InlineData("Movies/film.mkv")]
    [InlineData("downloads/042")]
    public async Task MoveOutCheck_OfAPathThisMountCannotDelete_IsRefused(string path)
    {
        _client.Add(Item(42));

        var error = (await _sut.MoveOutCheckAsync(path, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Retryable.ShouldBeFalse();
    }

    // The agent sees this refusal on fs_move, where it never asked about deleting anything —
    // fs_move_out_check is invisible to it. So the envelope must first say the move is what cannot
    // finish, and only then give the tail delete's own reason for refusing.
    [Fact]
    public async Task MoveOutCheck_OfAnOrdinaryMediaPath_FramesTheDeleteRefusalAsTheMoveThatCannotFinish()
    {
        var check = (await _sut.MoveOutCheckAsync("Movies/film.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Err>().Error;
        var delete = (await _sut.DeleteAsync("Movies/film.mkv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error;

        check.ErrorCode.ShouldBe(delete.ErrorCode);
        check.Message.ShouldContain("'Movies/film.mkv'");
        check.Message.ShouldContain("deleting the source");
        check.Message.ShouldContain(delete.Message);
        check.Hint.ShouldNotBeNull();
        check.Hint.ShouldContain("fs_copy");
    }

    // What may still leave: a leftover download directory and a leftover status file, which are the
    // two things this mount's delete removes.
    [Theory]
    [InlineData("downloads/99")]
    [InlineData("downloads/99/status.json")]
    public async Task MoveOutCheck_OfALeftoverThisMountCanDelete_IsAllowed(string path)
    {
        _client.Add(Item(42));
        await WriteLeftoverStatus();

        (await _sut.MoveOutCheckAsync(path, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Ok>();
    }

    // The disk glob relativizes an absolute pattern against the root it matches from; the overlay
    // used to match the caller's original spelling, whose leading root no mount-relative candidate
    // can ever have. So an absolute pattern listed the real files and silently omitted every
    // virtual status.json — the one thing on this mount that only the overlay knows about.
    [Theory]
    [InlineData("downloads/*/status.json", "downloads/42/status.json")]
    [InlineData("downloads/*", "downloads/42/")]
    public async Task Glob_WithAnAbsolutePattern_StillListsWhatTheOverlayOwns(string relative, string expected)
    {
        _client.Add(Item(42));
        _disk.GlobResults.Add(Path.Combine(_libraryRoot, "downloads", "42", "payload.mkv"));

        var absolute = Path.Combine(_libraryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var glob = (await _sut.GlobAsync("", absolute, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain(expected);
        glob.Entries.ShouldContain("downloads/42/payload.mkv");
    }

    [Fact]
    public async Task Glob_WithARelativePattern_IsUnchanged()
    {
        _client.Add(Item(42));

        var glob = (await _sut.GlobAsync("downloads", "*/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("downloads/42/status.json");
    }

    // The same boundary, crossed by the one path that never calls this backend's MoveAsync: a
    // cross-mount move streams the payload files out and then deletes the source, and that delete is
    // the overlay's cancel. So the move the same-mount path refuses outright used to complete —
    // download cancelled, files gone — with a copy left behind on the other mount.
    [Fact]
    public async Task CrossMountMove_OfALiveDownload_IsRefusedAndCancelsNothing()
    {
        _client.Add(Item(42));
        var destination = new Mock<IFileSystemBackend>();
        destination.SetupGet(b => b.FilesystemName).Returns("vault");

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/media/downloads/42"))
            .Returns(Resolved(_sut, "downloads/42"));
        registry.Setup(r => r.Resolve("/vault/42"))
            .Returns(Resolved(destination.Object, "42"));

        var result = await new VfsMoveTool(registry.Object).RunAsync("/media/downloads/42", "/vault/42");

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("has not finished");
        _client.CleanedUp.ShouldBeEmpty();
        _client.Items.ShouldContain(i => i.Id == 42);
        destination.Verify(b => b.WriteChunksAsync(It.IsAny<string>(),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The reverse direction: a move landing inside a live download's directory is destroyed the
    // moment the download is cancelled, whichever mount it came from.
    [Fact]
    public async Task CrossMountMove_IntoALiveDownload_IsRefused()
    {
        _client.Add(Item(42));
        // The vault has no move-out rule, so the refusal can only come from the landing end.
        var source = new Mock<IFileSystemBackend>().AllowingMoveOut();
        source.SetupGet(b => b.FilesystemName).Returns("vault");
        source.Setup(b => b.InfoAsync("note.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(
                new FsInfoResult { Exists = true, Path = "note.md", IsDirectory = false }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/note.md")).Returns(Resolved(source.Object, "note.md"));
        registry.Setup(r => r.Resolve("/media/downloads/42/note.md"))
            .Returns(Resolved(_sut, "downloads/42/note.md"));

        var result = await new VfsMoveTool(registry.Object)
            .RunAsync("/vault/note.md", "/media/downloads/42/note.md");

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("live download");
        source.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A leftover is an ordinary file, so it leaves the mount like one: the check allows it, the
    // bytes stream, and the tail delete removes the real file the disk owns.
    [Fact]
    public async Task CrossMountMove_OfALeftoverStatusFile_StillTransfers()
    {
        _client.Add(Item(42));
        await WriteLeftoverStatus();
        var destination = new Mock<IFileSystemBackend>();
        destination.SetupGet(b => b.FilesystemName).Returns("vault");
        destination.Setup(b => b.WriteChunksAsync("status.json",
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(14L);

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/media/downloads/99/status.json"))
            .Returns(Resolved(_sut, "downloads/99/status.json"));
        registry.Setup(r => r.Resolve("/vault/status.json"))
            .Returns(Resolved(destination.Object, "status.json"));

        var result = await new VfsMoveTool(registry.Object)
            .RunAsync("/media/downloads/99/status.json", "/vault/status.json");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        _disk.TrashedPaths.ShouldContain(p => p.EndsWith("status.json", StringComparison.Ordinal));
        _client.CleanedUp.ShouldBeEmpty();
    }

    // A copy leaves the source in place, so the download keeps its files and nothing is orphaned:
    // taking a snapshot of what has arrived so far stays possible. Only the move is refused.
    [Fact]
    public async Task CrossMountCopy_OutOfALiveDownload_StillTransfers()
    {
        _client.Add(Item(42));
        Directory.CreateDirectory(Path.Combine(_libraryRoot, "downloads", "42"));
        await File.WriteAllTextAsync(Path.Combine(_libraryRoot, "downloads", "42", "payload.mkv"), "half");
        var destination = new Mock<IFileSystemBackend>();
        destination.SetupGet(b => b.FilesystemName).Returns("vault");
        destination.Setup(b => b.WriteChunksAsync("payload.mkv",
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(4L);

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/media/downloads/42/payload.mkv"))
            .Returns(Resolved(_sut, "downloads/42/payload.mkv"));
        registry.Setup(r => r.Resolve("/vault/payload.mkv"))
            .Returns(Resolved(destination.Object, "payload.mkv"));

        var result = await new VfsCopyTool(registry.Object)
            .RunAsync("/media/downloads/42/payload.mkv", "/vault/payload.mkv");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "payload.mkv")).ShouldBeTrue();
        _client.Items.ShouldContain(i => i.Id == 42);
    }

    // An ordinary media file's move off this mount ends on a delete this mount refuses, so the
    // whole move is refused before the first byte. It used to stream the file — gigabytes, for the
    // media this mount holds — and then answer "copied, but the source could not be removed",
    // leaving the agent with a duplicate it never asked for and a move it can never complete.
    [Fact]
    public async Task CrossMountMove_OfAPlainMediaFile_IsRefusedBeforeAnythingIsStreamed()
    {
        _client.Add(Item(42));
        await File.WriteAllTextAsync(Path.Combine(_libraryRoot, "notes.txt"), "hello");
        var destination = new Mock<IFileSystemBackend>();
        destination.SetupGet(b => b.FilesystemName).Returns("vault");

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/media/notes.txt")).Returns(Resolved(_sut, "notes.txt"));
        registry.Setup(r => r.Resolve("/vault/notes.txt")).Returns(Resolved(destination.Object, "notes.txt"));

        var result = await new VfsMoveTool(registry.Object).RunAsync("/media/notes.txt", "/vault/notes.txt");

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        result["message"]!.GetValue<string>().ShouldContain("only removes download directories");
        File.Exists(Path.Combine(_libraryRoot, "notes.txt")).ShouldBeTrue();
        destination.Verify(b => b.WriteChunksAsync(It.IsAny<string>(),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A real file left at downloads/<id>/status.json after its download is gone used to be a ghost:
    // the overlay answered every info for that path with its virtual file (exists=false once no
    // download owns the id) and refused every delete as read-only, so nothing could see or remove
    // it. With no live item the disk underneath is the truth.
    [Fact]
    public async Task Info_ALeftoverStatusFileWithNoLiveDownload_ComesFromDisk()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");

        var info = (await _sut.InfoAsync("downloads/99/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(false);
    }

    [Fact]
    public async Task Delete_ALeftoverStatusFileWithNoLiveDownload_RemovesTheRealFile()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");

        (await _sut.DeleteAsync("downloads/99/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();

        _disk.TrashedPaths.ShouldContain(p => p.EndsWith("status.json", StringComparison.Ordinal));
    }

    // Delete refuses two things and does everything else. Removing a live download's status file
    // would dismantle part of a download the agent meant to leave running, and every path that is
    // neither a download directory nor a status file is outside what delete means here.
    [Theory]
    [InlineData("downloads/42/status.json", "read-only")]
    [InlineData("downloads/42/payload.mkv", "only removes download directories")]
    [InlineData("Movies/film.mkv", "only removes download directories")]
    [InlineData("downloads/042", "only removes download directories")]
    public async Task Delete_ARefusedMediaPath_CancelsNothing(string path, string reason)
    {
        _client.Add(Item(42));

        var error = (await _sut.DeleteAsync(path, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain(reason);
        _client.CleanedUp.ShouldBeEmpty();
        _disk.RemovedDirectories.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("downloads/42")]
    [InlineData("downloads/./42")]
    [InlineData("downloads/43/../42")]
    public async Task Delete_ALiveDownloadDirectory_CancelsIt(string path)
    {
        _client.Add(Item(42));

        (await _sut.DeleteAsync(path, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value.Message.ShouldContain("cancelled");

        _client.CleanedUp.ShouldContain(42);
    }

    [Fact]
    public async Task Delete_ADownloadDirectoryThatExistsNowhere_IsNotFound()
    {
        (await _sut.DeleteAsync("downloads/99", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    // The rendered view wins for as long as a download owns the id, so info reports its size
    // rather than the disk's answer for a file that is not there.
    [Fact]
    public async Task Info_ALiveDownloadsStatusFile_ReportsTheRenderedSize()
    {
        _client.Add(Item(42));

        var info = (await _sut.InfoAsync("downloads/42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        info.Exists.ShouldBeTrue();
        info.Size.ShouldNotBeNull();
    }

    // fs_info answers Exists=true for an active download's directory before qBittorrent has
    // created it on disk, so the glob's not_found short-circuit contradicted info on the very path
    // the mount's prose advertises. When the disk has nothing, the overlay still owns its answer.
    [Fact]
    public async Task Glob_AnActiveDownloadDirectoryNotYetOnDisk_ListsTheVirtualStatusFile()
    {
        _client.Add(Item(42));
        _disk.ThrowIfBaseMissing = true;

        var glob = (await _sut.GlobAsync("downloads/42", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("downloads/42/status.json");
    }

    [Fact]
    public async Task Glob_AMissingDirectoryNoDownloadOwns_IsStillNotFound()
    {
        _disk.ThrowIfBaseMissing = true;

        (await _sut.GlobAsync("Movies/nope", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    // A move into a live download's directory is refused because delete-as-cancel destroys
    // whatever landed there — and a copy or blob write lands a file in exactly the same place.
    // The original survives, but the copy is silently destroyed on cancel, so the boundary
    // refuses the way in for every write-shaped operation. The way out stays open: copying from
    // a live download leaves the download's own files untouched.
    [Fact]
    public async Task Copy_IntoALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));
        await File.WriteAllTextAsync(Path.Combine(_libraryRoot, "book.epub"), "book");

        (await _sut.CopyAsync("book.epub", "downloads/42/book.epub", overwrite: false,
                createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Err>()
            .Error.Message.ShouldContain("removed when the download is cancelled");

        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "book.epub")).ShouldBeFalse();
    }

    [Fact]
    public async Task BlobWrite_IntoALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));

        (await _sut.WriteBlobAsync("downloads/42/book.epub", Convert.ToBase64String("book"u8.ToArray()),
                offset: 0, overwrite: true, createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsBlobWriteResult>.Err>()
            .Error.Message.ShouldContain("live download");

        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "book.epub")).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteChunks_IntoALiveDownloadDirectory_IsRefused()
    {
        _client.Add(Item(42));

        var thrown = await Should.ThrowAsync<FileSystemOperationException>(() => _sut.WriteChunksAsync(
            "downloads/42/book.epub", Chunks("book"), overwrite: true, createDirectories: true,
            CancellationToken.None));

        thrown.Error.Message.ShouldContain("live download");
        File.Exists(Path.Combine(_libraryRoot, "downloads", "42", "book.epub")).ShouldBeFalse();
    }

    // A copy reads its source's bytes, so it asks the byte-read intent about the source end — the
    // same question fs_blob_read and the streamed read already ask. Without it, a copy of the one
    // path on this mount that has no bytes on disk fell through to the disk and answered not_found
    // for a file fs_info, fs_glob and fs_read all report as existing.
    [Fact]
    public async Task Copy_ALiveDownloadsStatusFileAsSource_IsRefusedAndPointsAtFsRead()
    {
        _client.Add(Item(42));

        var error = (await _sut.CopyAsync("downloads/42/status.json", "Movies/snapshot.json",
                overwrite: false, createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("fs_read");
        File.Exists(Path.Combine(_libraryRoot, "Movies", "snapshot.json")).ShouldBeFalse();
    }

    // A leftover status file is an ordinary file, so it copies like one.
    [Fact]
    public async Task Copy_ALeftoverStatusFileAsSource_StillCopies()
    {
        _client.Add(Item(42));
        await WriteLeftoverStatus();

        (await _sut.CopyAsync("downloads/99/status.json", "Movies/snapshot.json",
                overwrite: false, createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Ok>();

        (await File.ReadAllTextAsync(Path.Combine(_libraryRoot, "Movies", "snapshot.json")))
            .ShouldContain("stale");
    }

    [Fact]
    public async Task Copy_OutOfALiveDownloadDirectory_StillCopies()
    {
        _client.Add(Item(42));
        var payload = Path.Combine(_libraryRoot, "downloads", "42", "book.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        await File.WriteAllTextAsync(payload, "book");

        (await _sut.CopyAsync("downloads/42/book.epub", "Books/book.epub", overwrite: false,
                createDirectories: true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Ok>();

        File.Exists(Path.Combine(_libraryRoot, "Books", "book.epub")).ShouldBeTrue();
    }

    // The leftover fix made a dead download's real status.json visible and removable; reading it
    // still answered not_found (fs_read) or the read-only refusal (fs_blob_read) — info said the
    // file exists while both read paths denied it. With no live item the disk is the truth for
    // reads too.
    [Fact]
    public async Task Read_ALeftoverStatusFileWithNoLiveDownload_ServesTheRealFile()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");

        var read = (await _sut.ReadAsync("downloads/99/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("stale");
    }

    [Fact]
    public async Task Read_AStatusPathWithNoLiveDownloadAndNoFile_IsNotFound()
    {
        (await _sut.ReadAsync("downloads/99/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    private static FsResult<FileSystemResolution> Resolved(IFileSystemBackend backend, string relativePath) =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, ""));

    private async Task<string> Collect(string path)
    {
        var bytes = new List<byte>();
        await foreach (var chunk in _sut.ReadChunksAsync(path, CancellationToken.None))
        {
            bytes.AddRange(chunk.ToArray());
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task WriteLeftoverStatus()
    {
        var leftover = Path.Combine(_libraryRoot, "downloads", "99", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{\"stale\":true}");
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(string content)
    {
        await Task.CompletedTask;
        yield return Encoding.UTF8.GetBytes(content);
    }
}