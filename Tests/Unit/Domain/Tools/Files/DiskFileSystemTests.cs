using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class DiskFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"diskfs-{Guid.NewGuid():N}");

    public DiskFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private DiskFileSystem PlainRoot() =>
        new("media", "A plain disk root.", new LocalFileSystemClient(), new LibraryPathConfig(_root));

    private TextDiskFileSystem TextRoot() =>
        new("vault", "A text disk root.", new LocalFileSystemClient(), new LibraryPathConfig(_root),
            [".md", ".txt"]);

    // A plain disk root has no text tooling, so read, create, edit and search are left unoverridden
    // and the base answers them. That is what keeps /media from advertising operations it never had.
    [Fact]
    public async Task PlainRoot_TextOperations_ReturnUnsupported()
    {
        var fs = PlainRoot();

        (await fs.CreateAsync("note.md", "hi", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await fs.EditAsync("note.md", [], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await fs.SearchAsync("q", false, null, null, null, 50, 1,
                global::Domain.DTOs.VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    // Neither shape can run a command; only the sandbox adds that.
    [Fact]
    public async Task NeitherShape_SupportsExec()
    {
        (await PlainRoot().ExecAsync("", "ls", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
        (await TextRoot().ExecAsync("", "ls", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    [Fact]
    public async Task TextRoot_CreateReadEditSearch_RoundTripsOnDisk()
    {
        var fs = TextRoot();

        (await fs.CreateAsync("notes/todo.md", "# Todo\nbuy milk\n", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        var read = (await fs.ReadAsync("notes/todo.md", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("buy milk");

        (await fs.EditAsync("notes/todo.md", [new global::Domain.DTOs.TextEdit("milk", "bread")], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Ok>();

        var search = (await fs.SearchAsync("bread", false, null, "/", null, 50, 1,
                global::Domain.DTOs.VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.TotalMatches.ShouldBe(1);
    }

    [Fact]
    public async Task Info_ReportsDiskMetadata()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "abc");

        var info = (await PlainRoot().InfoAsync("a.txt", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(false);
        info.Size.ShouldBe(3);
    }

    [Fact]
    public async Task BlobChunks_RoundTripBytes()
    {
        var fs = PlainRoot();
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        await fs.WriteChunksAsync("blob.bin", Chunks(bytes), overwrite: true, createDirectories: true, CancellationToken.None);

        var readBack = new List<byte>();
        await foreach (var chunk in fs.ReadChunksAsync("blob.bin", CancellationToken.None))
        {
            readBack.AddRange(chunk.ToArray());
        }

        readBack.ShouldBe(bytes);
    }

    // What advertising fs_blob_write commits a backend to: the transfer driver sends one 256 KiB
    // chunk per wire call at an increasing offset, so anything bigger than a chunk arrives as a
    // sequence of ranged writes that must append rather than restart. A backend that cannot do this
    // — one that only streams, whose ranged default refuses every nonzero offset — does not
    // advertise the tool at all.
    [Fact]
    public async Task PlainRoot_RangedBlobWritesAtIncreasingOffsets_AppendTheWholeTransfer()
    {
        var fs = PlainRoot();
        var chunks = new byte[][] { [1, 2, 3], [4, 5, 6], [7, 8] };

        long offset = 0;
        foreach (var chunk in chunks)
        {
            var write = await fs.WriteBlobAsync("blob.bin", Convert.ToBase64String(chunk), offset,
                overwrite: offset == 0, createDirectories: true, CancellationToken.None);

            write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();
            offset += chunk.Length;
        }

        (await File.ReadAllBytesAsync(Path.Combine(_root, "blob.bin")))
            .ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    }

    // The allowed extensions govern what the agent may author as text — create, edit, read. Blob
    // writes are the transfer path: cross-mount copy and move stream through them, and a disk root
    // must take whatever file arrives (a .png attachment into the vault, a dataset into the
    // sandbox). Gating them by extension would make every disk root refuse exactly those transfers,
    // so blob writes are deliberately ungated on text roots too.
    [Fact]
    public async Task TextRoot_BlobWriteOfAnyExtension_Writes()
    {
        var fs = TextRoot();

        var write = await fs.WriteBlobAsync("photo.png", Convert.ToBase64String([1, 2, 3]), 0,
            overwrite: true, createDirectories: true, CancellationToken.None);

        write.ShouldBeOfType<FsResult<FsBlobWriteResult>.Ok>();
        File.Exists(Path.Combine(_root, "photo.png")).ShouldBeTrue();
    }

    // Reading bytes as text needs a rule about which files are text, and that rule is what the text
    // shape adds. A plain disk root has none, so it does not read — and does not advertise fs_read.
    [Fact]
    public async Task PlainRoot_DoesNotRead()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "abc");

        (await PlainRoot().ReadAsync("a.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode
            .ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bytes)
    {
        await Task.CompletedTask;
        yield return bytes;
    }
}