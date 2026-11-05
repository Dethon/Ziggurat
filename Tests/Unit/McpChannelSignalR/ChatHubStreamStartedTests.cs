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
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A person's message starts a stream every other browser on the topic should follow: a member
// reading a shared topic, the same person on their phone. Their clients resume a stream they are
// told started (StreamResumeEffect), replaying the message from the buffer; nothing told them, so
// a shared topic showed the other person's turn only after a reload.
public class ChatHubStreamStartedTests
{
    private const string TopicId = "topic-1";
    private const long ChatId = 7;
    private const long ThreadId = 42;

    private readonly SessionService _sessionService = new();
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly Mock<IHubNotificationSender> _sender = new();
    private readonly StreamService _streamService;
    private readonly ChatHub _hub;

    public ChatHubStreamStartedTests()
    {
        var streamService = _streamService = new StreamService(
            _sessionService,
            new Mock<IThreadStateStore>().Object,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        _hub = new ChatHub(
            _sessionService,
            streamService,
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

        _sessionService.StartSession(TopicId, "jonas", ChatId, ThreadId, "default", "Chat");
    }

    private void GivenTheTopic(string owner, bool shared) =>
        _store.Setup(s => s.GetTopicByChatIdAndThreadIdAsync("jonas", ChatId, ThreadId))
            .ReturnsAsync(new TopicMetadata(TopicId, ChatId, ThreadId, "jonas", "Chat", DateTimeOffset.UtcNow, null,
                OwnerId: owner, TenantId: "home", Shared: shared));

    private async Task SendAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in _hub.SendMessage(TopicId, "hola", null, null, null, cts.Token))
        {
        }
    }

    [Fact]
    public async Task APersonsMessageInASharedTopic_TellsTheSpaceItsStreamStarted()
    {
        GivenTheTopic(owner: "marta", shared: true);

        await SendAsync();

        _sender.Verify(s => s.SendToGroupAsync(
            "space:home:default", "OnStreamStarted",
            It.Is<StreamStartedNotification>(n => n.TopicId == TopicId && n.SpaceSlug == "default"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A private topic's activity is its owner's business: their other browsers and whoever sees
    // every conversation, never the space.
    [Fact]
    public async Task APersonsMessageInAPrivateTopic_TellsOnlyItsOwnerAndWhoeverSeesAll()
    {
        GivenTheTopic(owner: "fran", shared: false);

        await SendAsync();

        _sender.Verify(s => s.SendToGroupAsync(
            "space:home:default:user:fran", "OnStreamStarted", It.IsAny<StreamStartedNotification>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _sender.Verify(s => s.SendToGroupAsync(
            "space:home:default", It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // What the other browsers draw the message as: the name the person sent it under, beside the
    // account id that says whose it is.
    [Fact]
    public async Task APersonsMessage_GoesOnTheStreamUnderTheNameTheySentItUnder()
    {
        GivenTheTopic(owner: "marta", shared: true);
        _hub.Context.Items[ChatHub.DisplayNameItem] = "Fran";
        var (channel, _) = _streamService.GetOrCreateStream(TopicId, "hola", "fran", CancellationToken.None);
        var subscription = channel.Subscribe();

        await SendAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var userMessage = await subscription.ReadAllAsync(cts.Token).FirstAsync(m => m.UserMessage is not null, cts.Token);
        userMessage.UserMessage!.SenderId.ShouldBe("fran");
        userMessage.UserMessage.SenderName.ShouldBe("Fran");
    }

    // Two people sending in a shared topic at the same moment: the second send joins the stream the
    // first opened, and its browser reads that send's wire alone, so whatever the stream carried
    // before it subscribed was never shown there — Ana's page never showed Carlos's message, only the
    // reply to it, until a reload. A send joining a running stream is handed what it carried first.
    [Fact]
    public async Task ASendJoiningAStreamAlreadyRunning_IsHandedWhatItCarriedFirst()
    {
        GivenTheTopic(owner: "marta", shared: true);
        _streamService.GetOrCreateStream(TopicId, "vino", "carlos", CancellationToken.None);
        _streamService.TryIncrementPending(TopicId);
        await _streamService.WriteMessageAsync(TopicId, new ChatStreamMessage
        {
            Content = "vino", UserMessage = new UserMessageInfo("carlos", DateTimeOffset.UtcNow, "Carlos")
        });
        await _streamService.WriteMessageAsync(TopicId, new ChatStreamMessage { Content = "Apunt", MessageId = "m1" });

        var seen = new List<ChatStreamMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in _hub.SendMessage(TopicId, "hola", null, null, null, cts.Token))
        {
            seen.Add(message);
            if (message.UserMessage?.SenderId == "fran")
            {
                break;
            }
        }

        seen.Select(m => m.Content).ShouldBe(["vino", "Apunt", "hola"]);
    }

    // A page resuming a stream asks for its state, then for its wire: a message written between the
    // two was on neither, so the person who sent it at that moment was never shown there. The wire
    // starts with the people's messages the stream carried, which the page skips where it shows them.
    [Fact]
    public async Task AResumedStream_StartsWithThePeoplesMessagesItCarried()
    {
        GivenTheTopic(owner: "marta", shared: true);
        _streamService.GetOrCreateStream(TopicId, "vino", "carlos", CancellationToken.None);
        _streamService.TryIncrementPending(TopicId);
        await _streamService.WriteMessageAsync(TopicId, new ChatStreamMessage
        {
            Content = "vino", UserMessage = new UserMessageInfo("carlos", DateTimeOffset.UtcNow, "Carlos")
        });
        await _streamService.WriteMessageAsync(TopicId, new ChatStreamMessage { Content = "Apunt", MessageId = "m1" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await _hub.ResumeStream(TopicId, cts.Token).FirstAsync(cts.Token);

        first.UserMessage!.SenderId.ShouldBe("carlos");
        first.Content.ShouldBe("vino");
    }
}