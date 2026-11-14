using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A rename reaches whoever has the topic on screen, as sharing it does: Ana renamed a topic she shares
// with Carlos and his list and header kept the old name until the next reply ended there — forever, on
// a topic nobody wrote in again.
public class ChatHubRenameTopicTests
{
    private const string TopicId = "topic-1";
    private const long ChatId = 7;
    private const long ThreadId = 42;

    private readonly Mock<IThreadStateStore> _store = new();
    private readonly Mock<IHubNotificationSender> _sender = new();
    private readonly ChatHub _hub;

    public ChatHubRenameTopicTests()
    {
        var sessions = new SessionService();
        _hub = new ChatHub(
            sessions,
            new StreamService(sessions, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object,
                NullLogger<StreamService>.Instance),
            approvalService: null!,
            new ChannelNotificationEmitter(new ChannelInbox(new FakeTimeProvider()), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            _store.Object,
            pushSubscriptionStore: null!,
            attachmentService: null!,
            _sender.Object,
            new RetentionSettings(),
            new DictationSettings(),
            new VouchingAccounts(),
            new HouseholdSpacesAccounts(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new SignedInCaller()
        };
    }

    private TopicMetadata Stored(bool shared) =>
        new(TopicId, ChatId, ThreadId, "jonas", "Cena", DateTimeOffset.UtcNow, null, OwnerId: "fran", TenantId: "home",
            Shared: shared);

    [Fact]
    public async Task RenamingASharedTopic_TellsTheSpace()
    {
        var stored = Stored(shared: true);
        _store.Setup(s => s.GetTopicAsync("home", "jonas", ChatId, TopicId)).ReturnsAsync(stored);

        await _hub.SaveTopic(stored with { Name = "Cena del sábado" });

        _sender.Verify(s => s.SendToGroupAsync(
            "space:home:default", "OnTopicChanged",
            It.Is<TopicChangedNotification>(n => n.TopicId == TopicId && n.Topic!.Name == "Cena del sábado"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A private topic's name is its owner's business: their other devices and whoever sees all.
    [Fact]
    public async Task RenamingAPrivateTopic_TellsOnlyItsOwnerAndWhoeverSeesAll()
    {
        var stored = Stored(shared: false);
        _store.Setup(s => s.GetTopicAsync("home", "jonas", ChatId, TopicId)).ReturnsAsync(stored);

        await _hub.SaveTopic(stored with { Name = "Mío" });

        _sender.Verify(s => s.SendToGroupAsync(
            "space:home:default:user:fran", "OnTopicChanged", It.IsAny<TopicChangedNotification>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _sender.Verify(s => s.SendToGroupAsync(
            "space:home:default", It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}