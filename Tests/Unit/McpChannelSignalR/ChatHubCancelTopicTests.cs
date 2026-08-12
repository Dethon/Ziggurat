using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
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

// Cancelling ends the turn, and the tool call a prompt was gating ends with it. Left pending, the
// request waits for an answer nobody owes it: the agent's turn is already gone, the modal stays up
// over a stream that no longer exists, and the topic keeps reporting a prompt to every page that
// resumes it. Deleting a topic has always cleared them; cancelling one has to as well.
public class ChatHubCancelTopicTests : IDisposable
{
    private const string TopicId = "topic-1";

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly ApprovalService _approvalService;
    private readonly ChatHub _hub;

    public ChatHubCancelTopicTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        _approvalService = new ApprovalService(
            _streamService,
            _sessionService,
            new Mock<IHubNotificationSender>().Object,
            NullLogger<ApprovalService>.Instance);

        _hub = new ChatHub(
            _sessionService,
            _streamService,
            _approvalService,
            new ChannelNotificationEmitter(new ChannelInbox(new FakeTimeProvider()), DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            threadStore: null!,
            pushSubscriptionStore: null!,
            attachmentService: null!,
            new Mock<IHubNotificationSender>().Object,
            new RetentionSettings(),
            new DictationSettings(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller()
        };

        _sessionService.StartSession(TopicId, "jack", 7, 42);
    }

    [Fact]
    public async Task CancelTopic_EndsThePromptTheCancelledTurnWasWaitingOn()
    {
        _streamService.GetOrCreateStream(TopicId, "prompt", "fran", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "mcp__server__tool", new Dictionary<string, object?>())];

        var approvalTask = _approvalService.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "7:42", Mode = ApprovalMode.Request, Requests = requests });

        _approvalService.GetPendingApprovalForTopic(TopicId).ShouldNotBeNull();

        await _hub.CancelTopic(TopicId);

        (await approvalTask).ShouldBe("rejected");
        _approvalService.GetPendingApprovalForTopic(TopicId).ShouldBeNull();
    }

    public void Dispose() => _streamService.Dispose();
}