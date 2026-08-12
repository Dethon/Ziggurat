using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// The hub's topic methods used to be unreachable from a unit test: the constructor took the
// channel-local concrete store, so every hub test passed it null and could exercise nothing that
// touched a topic. Depending on the interface is what makes these two calls testable at all.
public sealed class ChatHubTopicTests : IDisposable
{
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly ChatHub _hub;

    public ChatHubTopicTests()
    {
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
            new ChannelNotificationEmitter(new ChannelInbox(new FakeTimeProvider()), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            _store.Object,
            new Mock<IPushSubscriptionStore>().Object,
            attachmentService: null!,
            new DictationSettings(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller()
        };
    }

    [Fact]
    public async Task SaveTopic_WritesThroughTheTopicStore()
    {
        var topic = new TopicMetadata(
            "topic-1", 7, 42, "jack", "Chat", new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), null);

        await _hub.SaveTopic(topic);

        _store.Verify(s => s.SaveTopicAsync(topic), Times.Once);
    }

    [Fact]
    public async Task GetHistory_ReadsThroughTheTopicStore()
    {
        IReadOnlyList<ChatHistoryMessage> stored =
            [new ChatHistoryMessage("m-1", "user", "hello", "fran", null, null)];
        _store.Setup(s => s.GetHistoryAsync("jack", 7, 42)).ReturnsAsync(stored);

        var history = await _hub.GetHistory("jack", 7, 42);

        history.ShouldHaveSingleItem().Content.ShouldBe("hello");
    }

    public void Dispose() => _streamService.Dispose();
}