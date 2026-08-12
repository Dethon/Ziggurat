using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.SignalR;
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
    private readonly Mock<IHubNotificationSender> _hubSender = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly ChatHub _hub;

    public ChatHubTopicTests()
    {
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();
        var attachments = new AttachmentService(
            attachmentSettings,
            new AttachmentTickets(attachmentSettings, time),
            new AttachmentStore(attachmentSettings, new RetentionSettings(), time, NullLogger<AttachmentStore>.Instance),
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
            new ChannelNotificationEmitter(new ChannelInbox(new FakeTimeProvider()), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            _store.Object,
            new Mock<IPushSubscriptionStore>().Object,
            attachments,
            _hubSender.Object,
            new RetentionSettings { PageSize = 2 },
            new DictationSettings(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller(),
            Groups = new Mock<IGroupManager>().Object
        };

        // A page call nobody scripted still has to come back with something usable, or a test
        // about the cursor fails inside the live-stream reporting that runs after it.
        _store.Setup(s => s.GetTopicPageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync(new TopicPage([], null));
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

    // The page size belongs to the retention policy, not to whoever is asking, so the ordinary
    // sidebar asks for nothing and gets what the operator configured.
    [Fact]
    public async Task GetTopicPage_WithNoPageSizeAsked_UsesTheConfiguredOne()
    {
        _store.Setup(s => s.GetTopicPageAsync("jack", "kitchen", null, 2))
            .ReturnsAsync(new TopicPage([], "42"));

        var page = await _hub.GetTopicPage("jack", "kitchen");

        page.NextCursor.ShouldBe("42");
    }

    // The client used to ask about every topic one at a time to find this out, which is the
    // second unbounded fan-out on start-up after history loading.
    [Fact]
    public async Task GetTopicPage_SaysWhichOfItsTopicsHaveAReplyInFlight()
    {
        _store.Setup(s => s.GetTopicPageAsync("jack", "kitchen", null, 2))
            .ReturnsAsync(new TopicPage([Topic("busy"), Topic("quiet")], null));
        _streamService.GetOrCreateStream("busy", "prompt", "fran", CancellationToken.None);

        var page = await _hub.GetTopicPage("jack", "kitchen");

        page.LiveTopicIds.ShouldBe(["busy"]);
    }

    private static TopicMetadata Topic(string topicId) => new(
        topicId, 7, 42, "jack", topicId, new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), null);

    [Fact]
    public async Task GetTopicPage_CarriesTheCursorThroughToTheStore()
    {
        await _hub.GetTopicPage("jack", "kitchen", cursor: "1700", pageSize: 5);

        _store.Verify(s => s.GetTopicPageAsync("jack", "kitchen", "1700", 5), Times.Once);
    }

    // The delete used to succeed and say nothing, so a second tab kept showing a row for a
    // conversation that no longer existed. Pagination makes that worse: the row will never be
    // refetched away, because paging only ever fetches backwards.
    [Fact]
    public async Task DeleteTopic_TellsTheSpaceTheTopicIsGone()
    {
        await _hub.JoinSpace("kitchen");

        await _hub.DeleteTopic("jack", "topic-1", 7, 42);

        _hubSender.Verify(s => s.SendToGroupAsync(
            "space:kitchen",
            "OnTopicChanged",
            It.Is<TopicChangedNotification>(n =>
                n.ChangeType == TopicChangeType.Deleted && n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Nothing is left to delete and the row may still be on another tab's screen, so the
    // broadcast is what makes the second delete harmless rather than something to guard against.
    [Fact]
    public async Task DeleteTopic_ForATopicThatIsAlreadyGone_StillBroadcastsTheSameRemoval()
    {
        await _hub.JoinSpace("kitchen");

        await _hub.DeleteTopic("jack", "topic-1", 7, 42);
        await _hub.DeleteTopic("jack", "topic-1", 7, 42);

        _hubSender.Verify(s => s.SendToGroupAsync(
            "space:kitchen",
            "OnTopicChanged",
            It.Is<TopicChangedNotification>(n => n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    public void Dispose()
    {
        _streamService.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}