using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.StateManagers;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Integration.McpChannelSignalR;

// Deleting a topic reaches Redis for the history and the topic record, so the whole call is
// exercised here rather than around it. Removing a conversation removes what was in it: the sweep
// would reach these files eventually, but deleting a topic is the person saying they want them
// gone now.
public sealed class ChatHubDeleteTopicTests : IClassFixture<RedisFixture>, IDisposable
{
    private const string TopicId = "topic-delete-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly AttachmentStore _store;
    private readonly ChatHub _hub;

    public ChatHubDeleteTopicTests(RedisFixture redis)
    {
        var settings = new AttachmentSettings { StoragePath = _root };
        _store = new AttachmentStore(settings, _time, NullLogger<AttachmentStore>.Instance);
        var attachments = new AttachmentService(
            settings,
            new AttachmentTickets(settings, _time),
            _store,
            NullLogger<AttachmentService>.Instance);

        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        var approvals = new ApprovalService(
            _streamService,
            _sessionService,
            new Mock<IHubNotificationSender>().Object,
            NullLogger<ApprovalService>.Instance);

        _hub = new ChatHub(
            _sessionService,
            _streamService,
            approvals,
            new ChannelNotificationEmitter(new ChannelInbox(_time), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            new RedisThreadStateStore(redis.Connection, new RetentionSettings(), _time),
            new Mock<IPushSubscriptionStore>().Object,
            attachments,
            new Mock<IHubNotificationSender>().Object,
            new RetentionSettings(),
            new DictationSettings(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller()
        };

        _sessionService.StartSession(TopicId, "jack", 771, 42, "default", "Chat");
    }

    public void Dispose()
    {
        _streamService.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteTopic_RemovesThatConversationsFilesAndLeavesOthersAlone()
    {
        var mine = await StoreAsync("771:42", "photo.png");
        var elsewhere = await StoreAsync("772:42", "other.png");

        await _hub.DeleteTopic("jack", TopicId, 771, 42);

        _store.Find(mine.Id).ShouldBeNull();
        _store.Find(elsewhere.Id).ShouldNotBeNull();
    }

    private async Task<AttachmentReference> StoreAsync(string conversationId, string fileName)
    {
        using var content = new MemoryStream("bytes"u8.ToArray());
        return await _store.SaveAsync(
            conversationId, "default", fileName, "image/png", content, CancellationToken.None);
    }
}