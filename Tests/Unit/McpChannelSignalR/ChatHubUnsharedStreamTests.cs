using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Security;
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

// Ana made a topic private while its reply streamed on Carlos's page: his list was told, but the hub
// kept sending him the reply as it was written. A reply stops reaching a person the moment they may no
// longer see its topic.
public class ChatHubUnsharedStreamTests
{
    private const string TopicId = "topic-1";
    private const long ChatId = 7;
    private const long ThreadId = 42;

    private readonly SessionService _sessions = new();
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly StreamService _streams;
    private TopicMetadata _topic = new(TopicId, ChatId, ThreadId, "jonas", "Cena", DateTimeOffset.UtcNow, null,
        OwnerId: "ana", TenantId: "home", Shared: true);

    public ChatHubUnsharedStreamTests()
    {
        _streams = new StreamService(_sessions, _store.Object, new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
        _store.Setup(s => s.GetTopicByChatIdAndThreadIdAsync("jonas", ChatId, ThreadId)).ReturnsAsync(() => _topic);
        _store.Setup(s => s.GetTopicAsync("home", "jonas", ChatId, TopicId)).ReturnsAsync(() => _topic);
        _store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>(), It.IsAny<bool>()))
            .Callback<TopicMetadata, bool>((saved, _) => _topic = saved).Returns(Task.CompletedTask);
        _sessions.StartSession(TopicId, "jonas", ChatId, ThreadId, "default", "Cena");
    }

    private ChatHub HubOf(string userId)
    {
        var caller = new SignedInCaller();
        caller.Items[ChatHub.AccountItem] = new ForwardedAccount(userId, "home", ["member"], SessionId: $"session-{userId}");
        return new ChatHub(
            _sessions, _streams,
            new ApprovalService(_streams, _sessions, _store.Object, new Mock<IHubNotificationSender>().Object,
                new Mock<IPushNotificationService>().Object, new AccountConnections(), NullLogger<ApprovalService>.Instance),
            new ChannelNotificationEmitter(new ChannelInbox(new FakeTimeProvider()), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object, _store.Object, pushSubscriptionStore: null!, attachmentService: null!,
            new Mock<IHubNotificationSender>().Object, new RetentionSettings(), new DictationSettings(),
            new VouchingAccounts(), new HouseholdSpacesAccounts(), NullLogger<ChatHub>.Instance)
        {
            Context = caller
        };
    }

    [Fact]
    public async Task ATopicMadePrivateWhileItsReplyStreams_StopsReachingTheOthersPage()
    {
        _streams.GetOrCreateStream(TopicId, "vino", "ana", CancellationToken.None);
        _streams.TryIncrementPending(TopicId);
        await _streams.WriteMessageAsync(TopicId, new ChatStreamMessage
        {
            Content = "vino", UserMessage = new UserMessageInfo("ana", DateTimeOffset.UtcNow, "Ana")
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var carlos = HubOf("carlos").ResumeStream(TopicId, cts.Token).GetAsyncEnumerator(cts.Token);
        (await carlos.MoveNextAsync()).ShouldBeTrue();

        await _streams.WriteMessageAsync(TopicId, new ChatStreamMessage { Content = "Antes", MessageId = "m1" });
        (await carlos.MoveNextAsync()).ShouldBeTrue();
        carlos.Current.Content.ShouldBe("Antes");

        await HubOf("ana").SetTopicShared("jonas", ChatId, TopicId, shared: false);
        await _streams.WriteMessageAsync(TopicId, new ChatStreamMessage { Content = "Después", MessageId = "m1" });

        (await carlos.MoveNextAsync()).ShouldBeFalse();
    }
}