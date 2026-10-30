using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.McpServers;
using Tests.Unit.McpChannelSignalR;

namespace Tests.Integration.Channels;

// The review copy's photos, across the boundary they really cross: the agent's channel connection
// hands a copy to WebChat's store_attachment over MCP, and the file is then downloadable from
// WebChat under the reference the shared history names it by (ADR 0046).
public sealed class McpChannelConnectionStoreAttachmentTests : IDisposable
{
    private static readonly AttachmentReference _photo = new()
    {
        Id = "11-22/aaa", FileName = "meal-1.jpg", MediaType = "image/jpeg", SizeBytes = 3
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly AttachmentService _attachments;

    public McpChannelConnectionStoreAttachmentTests()
    {
        var settings = new AttachmentSettings { StoragePath = _root };
        var store = new AttachmentStore(
            settings, new RetentionSettings(), new FakeConversationLiveness(), TimeProvider.System,
            NullLogger<AttachmentStore>.Instance);
        _attachments = new AttachmentService(
            settings, new AttachmentTickets(settings, TimeProvider.System), store, NullLogger<AttachmentService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ACopyHandedToWebChat_IsDownloadableThereUnderItsOwnReference()
    {
        await using var server = await StartWebChatAsync();
        await using var connection = new McpChannelConnection("signalr");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var kept = await connection.StoreAttachmentAsync("11:22", _photo, [1, 2, 3], CancellationToken.None);

        kept.ShouldBeTrue();
        _attachments.MintDownload(_photo.Id, "default").ShouldNotBeNull();
        (await _attachments.ReadBytesAsync(_photo.Id, CancellationToken.None)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    // Telegram keeps no files of its own, so its reference names the bot that fetches it — and the
    // conversation it was sent in, which is where WebChat files a review copy's file.
    [Fact]
    public async Task ATelegramPhotosCopy_IsDownloadableThereUnderTelegramsReference()
    {
        await using var server = await StartWebChatAsync();
        await using var connection = new McpChannelConnection("signalr");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);
        var photo = new AttachmentReference
        {
            Id = TelegramReference.For("-100200:-100200", "jack", "AgACAgQAAxkBAAIB-_photo"),
            FileName = "attachment-10.jpg", MediaType = "image/jpeg", SizeBytes = 3
        };

        var kept = await connection.StoreAttachmentAsync("-100200:-100200", photo, [1, 2, 3], CancellationToken.None);

        kept.ShouldBeTrue();
        _attachments.MintDownload(photo.Id, "default").ShouldNotBeNull();
        (await _attachments.ReadBytesAsync(photo.Id, CancellationToken.None)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task ACopyWebChatRefuses_IsNotKept()
    {
        await using var server = await StartWebChatAsync();
        await using var connection = new McpChannelConnection("signalr");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);
        await connection.StoreAttachmentAsync("11:22", _photo, [1, 2, 3], CancellationToken.None);

        var again = await connection.StoreAttachmentAsync("11:22", _photo, [9], CancellationToken.None);

        again.ShouldBeFalse();
        (await _attachments.ReadBytesAsync(_photo.Id, CancellationToken.None)).ShouldBe(new byte[] { 1, 2, 3 });
    }

    // A clip crosses as one tool call whose base64 body Kestrel caps at its default 30,000,000 bytes,
    // and the cap on a kept clip is chosen to fit under it: one at the cap goes through the agent's
    // real connection into the real tool, and one a byte over is refused, not lost in transit.
    [Fact]
    public async Task AClipAtTheCap_CrossesInOneCallAndIsKept_AndOneOverItIsNot()
    {
        var cap = new AttachmentSettings().MaxReviewCopyClipBytes;
        await using var server = await StartWebChatAsync();
        await using var connection = new McpChannelConnection("signalr");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var atCap = await connection.StoreAttachmentAsync(
            "11:22", Clip("11-22/clip", cap), new byte[cap], CancellationToken.None);
        var over = await connection.StoreAttachmentAsync(
            "11:22", Clip("11-22/over", cap + 1), new byte[cap + 1], CancellationToken.None);

        atCap.ShouldBeTrue();
        _attachments.MintDownload("11-22/clip", "default").ShouldNotBeNull();
        over.ShouldBeFalse();
        _attachments.MintDownload("11-22/over", "default").ShouldBeNull();
    }

    private static AttachmentReference Clip(string id, long size) => new()
    {
        Id = id, FileName = "meal.mp4", MediaType = "video/mp4", SizeBytes = size
    };

    // A channel that keeps no copies offers no such tool, and the answer is "not kept", not a fault.
    [Fact]
    public async Task AChannelWithNoStoreAttachmentTool_KeepsNothing()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FetchAttachmentTool>());
        await using var connection = new McpChannelConnection("telegram");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        (await connection.StoreAttachmentAsync("11:22", _photo, [1, 2, 3], CancellationToken.None)).ShouldBeFalse();
    }

    private Task<RunningServer> StartWebChatAsync() =>
        InMemoryMcpServer.StartAsync(services => services
            .AddSingleton(_attachments)
            .AddSingleton(new SessionService())
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<StoreAttachmentTool>());
}