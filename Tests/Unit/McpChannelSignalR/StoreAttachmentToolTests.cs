using Domain.DTOs;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// A review copy shows another channel's conversation, whose history names its files by that
// channel's references. The copy the agent hands over is kept under the very same reference, so a
// download minted for it from the topic's space works exactly as one for a file sent here does.
public sealed class StoreAttachmentToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly SessionService _sessions = new();
    private readonly AttachmentStore _store;
    private readonly AttachmentService _attachments;
    private readonly IServiceProvider _services;

    public StoreAttachmentToolTests()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var settings = new AttachmentSettings
        {
            StoragePath = _root, MaxBytesPerFile = 4096, MaxReviewCopyClipBytes = 8192
        };
        _store = new AttachmentStore(
            settings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance);
        _attachments = new AttachmentService(
            settings, new AttachmentTickets(settings, time), _store, NullLogger<AttachmentService>.Instance);
        _services = new ServiceCollection()
            .AddSingleton(_attachments)
            .AddSingleton(_sessions)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ACopyIsKeptUnderItsOwnReference_AndCanBeDownloadedFromTheTopicsSpace()
    {
        await StoreAsync("11-22/aaa", [1, 2, 3]);

        (await _attachments.ReadBytesAsync("11-22/aaa", CancellationToken.None)).ShouldBe(new byte[] { 1, 2, 3 });
        var record = _store.Find("11-22/aaa").ShouldNotBeNull();
        record.Reference.FileName.ShouldBe("meal-1.jpg");
        record.Reference.MediaType.ShouldBe("image/jpeg");
        record.Reference.SizeBytes.ShouldBe(3);
        _attachments.MintDownload("11-22/aaa", "default").ShouldNotBeNull()
            .Url.ShouldStartWith("/api/attachments/11-22/aaa?ticket=");
    }

    // The topic decides the space, as an upload's ticket does; a file kept for a topic elsewhere is
    // no download from the default space.
    [Fact]
    public async Task ACopyForATopicInAnotherSpace_IsKeptInThatSpace()
    {
        _sessions.StartSession("topic-1", "sugary", 11, 22, "household", "Meal estimate");

        await StoreAsync("11-22/aaa", [1, 2, 3]);

        _attachments.MintDownload("11-22/aaa", "default").ShouldBeNull();
        _attachments.MintDownload("11-22/aaa", "household").ShouldNotBeNull();
    }

    // The tool names its own file: one that already rests here — a person's upload, or the same
    // copy again — is never written over.
    [Fact]
    public async Task AReferenceThisStoreAlreadyHolds_IsRefusedAndLeftAsItWas()
    {
        await StoreAsync("11-22/aaa", [1, 2, 3]);

        await Should.ThrowAsync<McpException>(() => StoreAsync("11-22/aaa", [9, 9]));

        (await _attachments.ReadBytesAsync("11-22/aaa", CancellationToken.None)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory]
    [InlineData("../aaa")]
    [InlineData("11-22/../../etc")]
    [InlineData("aaa")]
    public async Task AReferenceThatCannotNameAFileHere_IsRefused(string id)
    {
        await Should.ThrowAsync<McpException>(() => StoreAsync(id, [1, 2, 3]));

        Directory.Exists(_root).ShouldBeFalse();
    }

    // A reference names its conversation in its first segment, and the copy is kept there: one
    // claiming another conversation's directory would land among that conversation's files and be
    // removed with its topic instead of this one's.
    [Fact]
    public async Task AReferenceUnderAnotherConversation_IsRefused()
    {
        await Should.ThrowAsync<McpException>(() => StoreAsync("33-44/aaa", [1, 2, 3]));

        _store.Find("33-44/aaa").ShouldBeNull();
        Directory.Exists(_root).ShouldBeFalse();
    }

    // The same rules a file sent here meets: the store holds only what the chat can show.
    [Fact]
    public async Task AFileThisChatWouldRefuse_IsRefused()
    {
        await Should.ThrowAsync<McpException>(() => StoreAsync("11-22/big", new byte[5000]));
        await Should.ThrowAsync<McpException>(() => StoreAsync("11-22/zip", [1], "application/zip"));

        _store.Find("11-22/big").ShouldBeNull();
        _store.Find("11-22/zip").ShouldBeNull();
    }

    // A review copy shows what a video model was sent, and that is the clip (ADR 0046's 2026-10-15
    // refinement). So the store keeps the channel's normalised clip, larger than a person's upload
    // may be, though nobody can upload one here.
    [Fact]
    public async Task AClip_IsKeptUnderItsOwnReference_AboveAnUploadsSizeButWithinAClipsOwn()
    {
        await StoreAsync("11-22/clip", new byte[6000], "video/mp4", "meal.mp4");

        var record = _store.Find("11-22/clip").ShouldNotBeNull();
        record.Reference.MediaType.ShouldBe("video/mp4");
        record.Reference.FileName.ShouldBe("meal.mp4");
        record.Reference.SizeBytes.ShouldBe(6000);
        _attachments.MintDownload("11-22/clip", "default").ShouldNotBeNull();
    }

    [Fact]
    public async Task AClipLargerThanAClipsCap_IsRefused()
    {
        await Should.ThrowAsync<McpException>(() => StoreAsync("11-22/clip", new byte[8193], "video/mp4", "meal.mp4"));

        _store.Find("11-22/clip").ShouldBeNull();
    }

    // Only what a channel sends after normalising it: a phone's own container never reaches the
    // store, so nothing the browser might fail to play rests here.
    [Fact]
    public async Task AVideoKindNoChannelSends_IsRefused()
    {
        await Should.ThrowAsync<McpException>(() => StoreAsync("11-22/mov", [1, 2, 3], "video/quicktime", "meal.mov"));

        _store.Find("11-22/mov").ShouldBeNull();
    }

    private Task<string> StoreAsync(
        string id, byte[] bytes, string mediaType = "image/jpeg", string fileName = "meal-1.jpg") =>
        StoreAttachmentTool.McpRun(
            "11:22", id, fileName, mediaType, Convert.ToBase64String(bytes), _services, CancellationToken.None);
}