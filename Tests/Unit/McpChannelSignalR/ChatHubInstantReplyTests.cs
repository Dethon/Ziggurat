using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A reply can be over before the hub has finished starting the send that asked for it: the agent
// answered in milliseconds while the hub was still telling the topic's other pages the stream started.
// The sending page must still be handed the reply its own wire subscribed for.
public class ChatHubInstantReplyTests
{
    private const string TopicId = "topic-1";
    private const long ChatId = 7;
    private const long ThreadId = 42;

    private readonly SessionService _sessionService = new();
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly Mock<IHubNotificationSender> _sender = new();
    private readonly ChannelInbox _inbox = new(TimeProvider.System);
    private readonly StreamService _streamService;
    private readonly ChatHub _hub;

    public ChatHubInstantReplyTests()
    {
        var streamService = _streamService = new StreamService(
            _sessionService,
            new Mock<IThreadStateStore>().Object,
            new Mock<IPushNotificationService>().Object,
            TimeProvider.System,
            NullLogger<StreamService>.Instance);
        var emitter = new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast);

        _hub = new ChatHub(
            _sessionService,
            streamService,
            approvalService: null!,
            emitter,
            new Mock<IAgentCatalog>().Object,
            _store.Object,
            pushSubscriptionStore: null!,
            attachmentService: HubParts.NoUploads(),
            _sender.Object,
            new RetentionSettings(),
            new DictationSettings(),
            HubParts.Turns(streamService, emitter, new VouchingAccounts()),
            HubParts.News(streamService, _store.Object, _sender.Object),
            new HouseholdSpacesAccounts(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new SignedInCaller()
        };

        _sessionService.StartSession(TopicId, "jonas", ChatId, ThreadId, "default", "Chat");
        _store.Setup(s => s.GetTopicByChatIdAndThreadIdAsync("jonas", ChatId, ThreadId))
            .ReturnsAsync(new TopicMetadata(TopicId, ChatId, ThreadId, "jonas", "Chat", DateTimeOffset.UtcNow, null,
                OwnerId: "fran", TenantId: "home", Shared: false));
    }

    // The agent: polls, and answers each message at once, its whole reply and its end.
    private async Task AnswerAtOnceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var batch = await _inbox.ReceiveAsync("agent", TimeSpan.FromSeconds(1), ct);
            foreach (var message in batch.Select(item => item.Message).OfType<ChannelMessageNotification>())
            {
                await _streamService.WriteReplyAsync(new SendReplyParams
                {
                    ConversationId = message.ConversationId, Content = "OK", ContentType = ReplyContentType.Text,
                    IsComplete = false, MessageId = "m1"
                });
                await _streamService.WriteReplyAsync(new SendReplyParams
                {
                    ConversationId = message.ConversationId, Content = "", ContentType = ReplyContentType.StreamComplete,
                    IsComplete = true, MessageId = "m1"
                });
            }
        }
    }

    [Fact]
    public async Task AReplyOverBeforeTheSendFinishedStarting_IsStillHandedToTheSendingPage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // The agent is polling before anything is sent, as it is in the deployment: its first poll
        // subscribes before the call returns its task.
        var agent = AnswerAtOnceAsync(cts.Token);
        // Telling the topic the stream started takes a while, and the reply is over by the time it is told.
        _sender.Setup(s => s.SendToGroupAsync(It.IsAny<string>(), "OnStreamStarted", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.Delay(300));

        var seen = new List<ChatStreamMessage>();
        await foreach (var chunk in _hub.SendMessage(TopicId, "hola", null, null, null, cts.Token))
        {
            seen.Add(chunk);
        }

        await cts.CancelAsync();
        await agent.ContinueWith(_ => { });
        seen.ShouldContain(m => m.Content == "OK", string.Join(" | ", seen.Select(m => m.Content ?? m.Error)));
        seen.ShouldContain(m => m.IsComplete);
    }
}