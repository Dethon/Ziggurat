using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
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

// Reaching the hub is not being somebody. A connection that never registered is refused on every
// call but registration itself, so it cannot read a transcript, change a topic or answer an
// approval it was never shown.
public sealed class ChatHubUnregisteredTests : IDisposable
{
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly ChatHub _hub;

    public ChatHubUnregisteredTests()
    {
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();
        var attachments = new AttachmentService(
            attachmentSettings,
            new AttachmentTickets(attachmentSettings, time),
            new AttachmentStore(attachmentSettings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance),
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
            new Mock<IHubNotificationSender>().Object,
            new RetentionSettings(),
            new DictationSettings(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new UnregisteredCaller(),
            Groups = new Mock<IGroupManager>().Object
        };
    }

    public static TheoryData<string, Func<ChatHub, Task>> Calls => new()
    {
        { "GetHistory", hub => hub.GetHistory("jack", 7, 42) },
        { "GetTopicPage", hub => hub.GetTopicPage("jack") },
        { "SearchTopics", hub => hub.SearchTopics("jack", "plans") },
        { "SaveTopic", hub => hub.SaveTopic(new TopicMetadata("topic-1", 7, 42, "jack", "Chat", DateTimeOffset.UnixEpoch, null)) },
        { "DeleteTopic", hub => hub.DeleteTopic("jack", "topic-1", 7, 42) },
        { "CancelTopic", hub => hub.CancelTopic("topic-1") },
        { "StartSession", Sync(hub => hub.StartSession("jack", "topic-1", 7, 42)) },
        { "RespondToApprovalAsync", Sync(hub => hub.RespondToApprovalAsync("approval-1", ToolApprovalResult.Approved)) },
        { "GetPendingApprovalForTopic", Sync(hub => hub.GetPendingApprovalForTopic("topic-1")) },
        { "IsApprovalPending", Sync(hub => hub.IsApprovalPending("approval-1")) },
        { "MarkTopicRead", hub => hub.MarkTopicRead("jack", 7, "topic-1") },
        { "IsProcessing", Sync(hub => hub.IsProcessing("topic-1")) },
        { "GetStreamState", Sync(hub => hub.GetStreamState("topic-1")) },
        { "JoinSpace", hub => hub.JoinSpace("default") },
        { "GetAgents", Sync(hub => hub.GetAgents()) },
        { "ValidateAgent", Sync(hub => hub.ValidateAgent("jack")) },
        { "GetAttachmentLimits", Sync(hub => hub.GetAttachmentLimits()) },
        { "CreateDictationTicket", Sync(hub => hub.CreateDictationTicket()) },
        { "CreateUploadTicket", Sync(hub => hub.CreateUploadTicket("topic-1")) },
        { "CreateAttachmentDownload", Sync(hub => hub.CreateAttachmentDownload("attachment-1")) },
        { "SubscribePush", hub => hub.SubscribePush(new PushSubscriptionDto("https://push.example/1", "key", "auth")) },
        { "ReplacePushSubscription", hub => hub.ReplacePushSubscription(new PushSubscriptionDto("https://push.example/2", "key", "auth"), "https://push.example/1") },
        { "UnsubscribePush", hub => hub.UnsubscribePush("https://push.example/1") },
    };

    // A synchronous hub method throws before there is a task to fault; the refusal is the same
    // answer to the caller either way, so it is handed back the way an async one would give it.
    private static Func<ChatHub, Task> Sync(Action<ChatHub> call) => hub =>
    {
        try
        {
            call(hub);
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task AnUnregisteredConnection_IsRefused(string method, Func<ChatHub, Task> call)
    {
        var refusal = await Should.ThrowAsync<HubException>(() => call(_hub), method);

        refusal.Message.ShouldContain("not registered");

        // Refused before anything is read or written, not after.
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AConnectionThatRegisters_IsLetThrough()
    {
        _store.Setup(s => s.GetHistoryAsync("jack", 7, 42)).ReturnsAsync([]);

        await _hub.RegisterUser("fran");
        var history = await _hub.GetHistory("jack", 7, 42);

        history.ShouldBeEmpty();
    }

    // The liveness probe reads nothing, and a client asks it of a connection it has just come
    // back to, before it has had the chance to register again.
    [Fact]
    public void Ping_AnswersAnUnregisteredConnection()
    {
        _hub.Ping().ShouldBeTrue();
    }

    // A stream answers the way SendMessage already does: one error chunk that ends it. Here the
    // topic is mid-reply, so a stream that let the caller in would have something to show.
    [Fact]
    public async Task ResumeStream_FromAnUnregisteredConnection_EndsWithTheRefusal()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        _streamService.GetOrCreateStream("topic-1", "hello", "fran", CancellationToken.None);

        // Bounded, so a stream that let the caller in fails here instead of waiting for a reply.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var chunks = await _hub.ResumeStream("topic-1", timeout.Token).ToListAsync();

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Error.ShouldNotBeNull().ShouldContain("not registered");
        chunk.IsComplete.ShouldBeTrue();
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