using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// One tool for reading a file whatever kind it is. Text behaves exactly as it always did; an image
// answers a small envelope and its bytes go to the store the send reads back, and every reason the
// picture cannot be shown is said out loud rather than left for the model to guess at.
public class VfsFileReadToolTests
{
    private const string ImagePath = "/vault/shots/error.png";
    private const string ImageRelative = "shots/error.png";

    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsFileReadTool _tool;

    public VfsFileReadToolTests()
    {
        _tool = new VfsFileReadTool(_registry.Object);
    }

    [Fact]
    public async Task ATextFile_ComesBackWithItsContentAndLineCount()
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
    public async Task OffsetAndLimitOnAText_ReachTheBackendUntouched()
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
    public async Task AMissingFile_ComesBackAsTheBackendsOwnErrorEnvelope()
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

    [Fact]
    public async Task AnImage_AnswersTheEnvelopeInTheCallersOwnCoordinates()
    {
        var tool = ImageTool([1, 2, 3, 4], out var store);

        var result = await tool.RunAsync(ImagePath);

        result!["filePath"]!.GetValue<string>().ShouldBe(ImagePath);
        result["mediaType"]!.GetValue<string>().ShouldBe("image/png");
        result["sizeBytes"]!.GetValue<long>().ShouldBe(4);
        result["shown"]!.GetValue<bool>().ShouldBeTrue();
        store.Written.ShouldHaveSingleItem();
    }

    // A distinct result shape, not the text read's fields repurposed: there are no lines to number
    // and no window to page through, so claiming either would be a lie the model could act on.
    [Fact]
    public async Task AnImage_AnswersADistinctShapeFromATextRead()
    {
        var result = await ImageTool([1, 2, 3, 4], out _).RunAsync(ImagePath);

        result!["content"].ShouldBeNull();
        result["totalLines"].ShouldBeNull();
        result["truncated"].ShouldBeNull();
    }

    [Theory]
    [InlineData("/vault/a.png", "image/png")]
    [InlineData("/vault/a.jpg", "image/jpeg")]
    [InlineData("/vault/a.jpeg", "image/jpeg")]
    [InlineData("/vault/a.gif", "image/gif")]
    [InlineData("/vault/a.webp", "image/webp")]
    [InlineData("/vault/A.PNG", "image/png")]
    public async Task EveryViewableExtension_TakesTheImagePath(string path, string expectedMediaType)
    {
        var relative = path["/vault/".Length..];
        _registry.Setup(r => r.Resolve(path)).Returns(Resolved(_backend.Object, relative));
        StatsAt(relative, 1);
        _backend.Setup(b => b.ReadChunksAsync(relative, It.IsAny<CancellationToken>()))
            .Returns(Chunks([9]));
        var tool = new VfsFileReadTool(_registry.Object, Support(new RecordingReadImageStore()));

        var result = await tool.RunAsync(path);

        result!["mediaType"]!.GetValue<string>().ShouldBe(expectedMediaType);
        _backend.Verify(
            b => b.ReadAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Kind is decided by extension, so everything that is not a viewable image is still a text read
    // — and the backend's own refusal, which names the file type and the extensions it does allow,
    // reaches the model unchanged rather than being re-worded here.
    [Fact]
    public async Task ANonImageBinary_TakesTheTextPathAndItsRefusalReachesTheModel()
    {
        _registry.Setup(r => r.Resolve("/vault/bundle.zip"))
            .Returns(Resolved(_backend.Object, "bundle.zip"));
        _backend.Setup(b => b.ReadAsync("bundle.zip", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FsError.Invalid<FsReadResult>("File type '.zip' not allowed. Allowed: .md, .txt"));
        var tool = new VfsFileReadTool(_registry.Object, Support(new RecordingReadImageStore()));

        var result = await tool.RunAsync("/vault/bundle.zip");

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["message"]!.GetValue<string>().ShouldContain(".zip");
    }

    // The refusal costs a stat, never the transfer: on a mount that can answer a size, an
    // over-the-ceiling file is refused before the first byte crosses the wire — the ceiling
    // exists so an enormous file cannot make one turn enormous, and pulling it in full just to
    // name its size is the cost it exists to prevent.
    [Fact]
    public async Task AnImageOverTheCeiling_IsRefusedByTheStatAloneAndNamesTheSizeAndTheLimit()
    {
        var tool = ImageTool(new byte[64], out var store, maxBytes: 32);

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["sizeBytes"]!.GetValue<long>().ShouldBe(64);
        var note = result["note"]!.GetValue<string>();
        note.ShouldContain("64");
        note.ShouldContain("32");
        store.Written.ShouldBeEmpty();
        _backend.Verify(
            b => b.ReadChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Every refusal decidable without the bytes — no vision, no store, no call id, no conversation
    // — is decided without them, and the envelope still names the size the mount reported.
    [Fact]
    public async Task ARefusalThatNeedsNoBytes_DoesNotTransferThem()
    {
        var tool = ImageTool([1, 2, 3, 4], out _, acceptsImages: false);

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["sizeBytes"]!.GetValue<long>().ShouldBe(4);
        _backend.Verify(
            b => b.ReadChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A mount that cannot stat is read — but the pull stops the moment the ceiling is passed,
    // so an oversized file costs one ceiling's worth of transfer, not the whole file. The
    // saving is paid for with the exact size: the envelope names the count as a floor.
    [Fact]
    public async Task AMountThatCannotStat_StopsPullingAtTheCeiling()
    {
        _registry.Setup(r => r.Resolve(ImagePath)).Returns(Resolved(_backend.Object, ImageRelative));
        _backend.Setup(b => b.InfoAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .ReturnsAsync(FsError.Fail<FsInfoResult>(
                ToolError.Codes.UnsupportedOperation, "fs_info is not supported"));
        var pulled = 0;
        _backend.Setup(b => b.ReadChunksAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .Returns(CountingChunks(chunkSize: 8, chunks: 1000, () => pulled++));
        var tool = new VfsFileReadTool(_registry.Object, Support(new RecordingReadImageStore(), maxBytes: 32));

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["sizeBytes"]!.GetValue<long>().ShouldBe(40);
        result["note"]!.GetValue<string>().ShouldContain("at least 40");
        pulled.ShouldBe(5);
    }

    [Fact]
    public async Task AModelThatDoesNotAcceptImages_IsToldSoRatherThanShownOne()
    {
        var tool = ImageTool([1, 2, 3, 4], out var store, acceptsImages: false);

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["note"]!.GetValue<string>().ShouldContain("does not accept images");
        store.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task AHostWithNoStore_SaysImagesCannotBeShownHere()
    {
        _registry.Setup(r => r.Resolve(ImagePath)).Returns(Resolved(_backend.Object, ImageRelative));
        StatsAt(ImageRelative, 4);
        _backend.Setup(b => b.ReadChunksAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .Returns(Chunks([1, 2, 3, 4]));
        var tool = new VfsFileReadTool(_registry.Object, Support(store: null));

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["sizeBytes"]!.GetValue<long>().ShouldBe(4);
        result["note"]!.GetValue<string>().ShouldContain("cannot show images");
    }

    // A tool built with no image support at all is the same host, answering the same way: the
    // conformance and feature suites construct the tool that way, and so does any harness.
    [Fact]
    public async Task AToolBuiltWithNoImageSupport_StillAnswersTheEnvelopeHonestly()
    {
        _registry.Setup(r => r.Resolve(ImagePath)).Returns(Resolved(_backend.Object, ImageRelative));
        StatsAt(ImageRelative, 4);
        _backend.Setup(b => b.ReadChunksAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .Returns(Chunks([1, 2, 3, 4]));

        var result = await _tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["filePath"]!.GetValue<string>().ShouldBe(ImagePath);
    }

    // A habit carried over from reading text must not cost the model a turn.
    [Fact]
    public async Task OffsetAndLimitOnAnImage_AreIgnoredAndTheEnvelopeSaysSo()
    {
        var tool = ImageTool([1, 2, 3, 4], out _);

        var result = await tool.RunAsync(ImagePath, offset: 10, limit: 20);

        result!["shown"]!.GetValue<bool>().ShouldBeTrue();
        result["note"]!.GetValue<string>().ShouldContain("ignored");
        _backend.Verify(
            b => b.ReadAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnImageThatWasShown_WritesItsBytesUnderTheConversationAndCallId()
    {
        var tool = ImageTool([7, 7, 7], out var store, callId: "call-42");

        await tool.RunAsync(ImagePath);

        var (conversationId, callId, image) = store.Written.ShouldHaveSingleItem();
        conversationId.ShouldBe("conv-1");
        callId.ShouldBe("call-42");
        image.VirtualPath.ShouldBe(ImagePath);
        image.MediaType.ShouldBe("image/png");
        image.Bytes.ShouldBe([7, 7, 7]);
    }

    // Nothing is stored in any case the envelope reports as not shown, so there are never bytes
    // waiting for a send that will not look for them.
    // A tool running outside a function-invocation context has nowhere to key the bytes. That is a
    // fourth reason, and wearing the host-limitation words told the model something false about the
    // deployment.
    [Fact]
    public async Task AnImageReadOutsideAToolCall_SaysThatRatherThanBlamingTheHost()
    {
        var tool = ImageTool([1, 2, 3, 4], out var store, callId: null);

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        var note = result["note"]!.GetValue<string>();
        note.ShouldNotContain("cannot show images");
        note.ShouldContain("tool call");
        store.Written.ShouldBeEmpty();
    }

    // The send looks the bytes up under the conversation id the turn's own context carries. A run
    // with no context — a harness, a benchmark — has no key any send will ever ask for, so storing
    // would strand the bytes and the envelope's "shown" would invite the model to wait for a
    // picture that cannot arrive, then re-read forever when it is told to try again.
    [Fact]
    public async Task ATurnCarryingNoConversationContext_RefusesRatherThanStrandingTheBytes()
    {
        var tool = ImageTool([1, 2, 3, 4], out var store, conversationId: null);

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["note"]!.GetValue<string>().ShouldContain("conversation");
        store.Written.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("over the ceiling")]
    [InlineData("no image capability")]
    [InlineData("no call id")]
    [InlineData("no conversation context")]
    public async Task AnImageThatCannotBeShown_WritesNothing(string reason)
    {
        var tool = ImageTool(
            new byte[64], out var store,
            maxBytes: reason == "over the ceiling" ? 32 : ReadImageSupport.DefaultMaxBytes,
            acceptsImages: reason != "no image capability",
            callId: reason == "no call id" ? null : "call-1",
            conversationId: reason == "no conversation context" ? null : "conv-1");

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["note"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        store.Written.ShouldBeEmpty();
    }

    // The three mounts with no bytes behind them render JSON and markdown and can hold no image
    // file, but a path spelled with an image extension still reaches them.
    [Fact]
    public async Task AMountThatHoldsNoBytes_RefusesRatherThanRenderingGarbage()
    {
        _registry.Setup(r => r.Resolve(ImagePath)).Returns(Resolved(_backend.Object, ImageRelative));
        // A rendered mount stats what it renders, so the refusal comes from the pull, not the stat.
        StatsAt(ImageRelative, 9);
        _backend.Setup(b => b.ReadChunksAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .Throws(new NotSupportedException("blob_read is not supported by this filesystem"));
        var tool = new VfsFileReadTool(_registry.Object, Support(new RecordingReadImageStore()));

        var result = await tool.RunAsync(ImagePath);

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    // The wire backend never throws NotSupportedException: every refusal a server answers — a
    // missing file, a jailed path, a mount with no bytes behind it — travels as the typed exception
    // carrying the backend's own envelope. That envelope reaches the model, as it does for a
    // transfer, rather than escaping the tool as a generic function failure.
    [Fact]
    public async Task ABackendsTypedRefusal_ReachesTheModelAsItsOwnEnvelope()
    {
        _registry.Setup(r => r.Resolve(ImagePath)).Returns(Resolved(_backend.Object, ImageRelative));
        // The wire server refuses the stat the same way it refuses the read; only the read's
        // refusal is the one the model sees.
        _backend.Setup(b => b.InfoAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileSystemOperationException(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.NotFound,
                Message = "fs_info failed: Path not found: shots/error.png",
                Retryable = false
            }));
        _backend.Setup(b => b.ReadChunksAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .Throws(new FileSystemOperationException(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.NotFound,
                Message = "fs_blob_read failed: Path not found: shots/error.png",
                Retryable = false
            }));
        var tool = new VfsFileReadTool(_registry.Object, Support(new RecordingReadImageStore()));

        var result = await tool.RunAsync(ImagePath);

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        result["message"]!.GetValue<string>().ShouldContain("Path not found");
    }

    // A zero-byte file has no picture in it. Showing the provider an empty image block is a request
    // it rejects, so the honest envelope says the file is empty and stores nothing.
    [Fact]
    public async Task AnEmptyFile_IsNotShownAndSaysThereIsNoImageInIt()
    {
        var tool = ImageTool([], out var store);

        var result = await tool.RunAsync(ImagePath);

        result!["shown"]!.GetValue<bool>().ShouldBeFalse();
        result["sizeBytes"]!.GetValue<long>().ShouldBe(0);
        result["note"]!.GetValue<string>().ShouldContain("empty");
        store.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnmountedImagePath_AnswersTheRegistrysOwnEnvelope()
    {
        _registry.Setup(r => r.Resolve("/nowhere/a.png"))
            .Returns(FsError.NotFound<FileSystemResolution>("/nowhere/a.png"));

        var result = await ImageTool([1], out _).RunAsync("/nowhere/a.png");

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void TheToolDescription_TellsTheModelItReadsTextAndImages()
    {
        VfsFileReadTool.ToolDescription.ShouldContain("image");
        VfsFileReadTool.ToolDescription.ShouldContain("text");
    }

    private VfsFileReadTool ImageTool(
        byte[] bytes,
        out RecordingReadImageStore store,
        long maxBytes = ReadImageSupport.DefaultMaxBytes,
        bool acceptsImages = true,
        string? callId = "call-1",
        string? conversationId = "conv-1")
    {
        store = new RecordingReadImageStore();
        _registry.Setup(r => r.Resolve(ImagePath)).Returns(Resolved(_backend.Object, ImageRelative));
        StatsAt(ImageRelative, bytes.Length);
        _backend.Setup(b => b.ReadChunksAsync(ImageRelative, It.IsAny<CancellationToken>()))
            .Returns(Chunks(bytes));
        return new VfsFileReadTool(
            _registry.Object, Support(store, maxBytes, acceptsImages, callId, conversationId));
    }

    // Every disk root that can serve an image can stat it; the statless mount is its own test.
    private void StatsAt(string relativePath, long size) =>
        _backend.Setup(b => b.InfoAsync(relativePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsInfoResult>.Ok(new FsInfoResult
            {
                Exists = true, Path = relativePath, Size = size
            }));

    private static ReadImageSupport Support(
        IReadImageStore? store,
        long maxBytes = ReadImageSupport.DefaultMaxBytes,
        bool acceptsImages = true,
        string? callId = "call-1",
        string? conversationId = "conv-1") =>
        new()
        {
            Store = store,
            CurrentCallId = () => callId,
            CurrentConversationId = () => conversationId,
            ModelAcceptsImages = () => acceptsImages,
            MaxBytes = maxBytes
        };

    // Two chunks so the ceiling is enforced across a stream rather than against one buffer.
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(
        byte[] bytes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        var split = bytes.Length / 2;
        if (split > 0)
        {
            yield return bytes[..split];
        }

        ct.ThrowIfCancellationRequested();
        yield return bytes[split..];
    }

    // A stream long enough that finishing it would be visible: the pull counter says exactly how
    // far the tool read before it stopped asking.
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> CountingChunks(
        int chunkSize, int chunks, Action onPull, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        for (var i = 0; i < chunks && !ct.IsCancellationRequested; i++)
        {
            onPull();
            yield return new byte[chunkSize];
        }
    }

    private static FsResult<FileSystemResolution> Resolved(IFileSystemBackend backend, string relativePath) =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath));

    private sealed class RecordingReadImageStore : IReadImageStore
    {
        public List<(string ConversationId, string CallId, ReadImage Image)> Written { get; } = [];

        public Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct)
        {
            Written.Add((conversationId, callId, image));
            return Task.CompletedTask;
        }

        public Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.FromResult<ReadImage?>(null);

        public Task DeleteAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}