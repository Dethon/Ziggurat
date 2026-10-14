using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
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