using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class ApprovalServiceTests : IDisposable
{
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IHubNotificationSender> _hubSender = new();
    private readonly Mock<ILogger<StreamService>> _streamLogger = new();
    private readonly Mock<IPushNotificationService> _push = new();
    private readonly AccountConnections _connections = new();
    private readonly ApprovalService _sut;

    public ApprovalServiceTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IThreadStateStore>().Object,
            new Mock<IPushNotificationService>().Object,
            _streamLogger.Object);

        _sut = new ApprovalService(
            _streamService,
            _sessionService,
            new Mock<IThreadStateStore>().Object,
            _hubSender.Object,
            _push.Object,
            _connections,
            new Mock<ILogger<ApprovalService>>().Object);
    }

    private static readonly Account _fran = new("home", "fran");

    private static IReadOnlyList<ToolApprovalRequest> Buy() =>
        [new ToolApprovalRequest("msg-1", "mcp__shop__buy", new Dictionary<string, object?>())];

    private static OfferApprovalParams Offer(string approvalId) => new()
    {
        ApprovalId = approvalId, ConversationId = "31:41", Asker = _fran, AgentId = "jonas", Requests = Buy()
    };

    // The question's own id is the one it is answered and settled by, so the relay can name it here.
    [Fact]
    public void RequestApprovalAsync_KeepsTheQuestionUnderItsOwnId_AndNamesItsAsker()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var approvalId = Guid.NewGuid().ToString("N");

        _ = _sut.RequestApprovalAsync(
            new RequestApprovalParams
            {
                ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = Buy(), ApprovalId = approvalId
            },
            _fran);

        var pending = _sut.GetPendingApprovalForTopic("topic1").ShouldNotBeNull();
        pending.ApprovalId.ShouldBe(approvalId);
        pending.AskedBy.ShouldBe("fran");
        _sut.AskerOf(approvalId).ShouldBe(_fran);
    }

    // Answered on another channel, the question still lets through what it asked about here — the
    // approved call is written into the topic as if it had been answered on the card — and every
    // browser showing the card is told it was answered elsewhere.
    [Fact]
    public async Task SettleApprovalAsync_ApprovedElsewhere_WritesTheCallsAndSaysAnsweredElsewhere()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        var (channel, _) = _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var reader = channel.Subscribe();
        var approvalId = Guid.NewGuid().ToString("N");
        var asked = _sut.RequestApprovalAsync(
            new RequestApprovalParams
            {
                ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = Buy(), ApprovalId = approvalId
            },
            _fran);

        await _sut.SettleApprovalAsync(approvalId, ToolApprovalResult.Approved);

        (await asked).ShouldBe("approved");
        (await reader.ReadAsync()).ApprovalRequest.ShouldNotBeNull();
        (await reader.ReadAsync()).ToolCalls.ShouldNotBeNullOrEmpty();
        _hubSender.Verify(sender => sender.SendAsync(
                "OnApprovalResolved",
                new ApprovalResolvedNotification("topic1", approvalId, null, true),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Called off — answered elsewhere before the settle could name it, or the turn stopped — the
    // card is taken down like an answered one, and nothing is left pending.
    [Fact]
    public async Task RequestApprovalAsync_CalledOff_TakesTheCardDown()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        using var calledOff = new CancellationTokenSource();
        var approvalId = Guid.NewGuid().ToString("N");
        var asked = _sut.RequestApprovalAsync(
            new RequestApprovalParams
            {
                ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = Buy(), ApprovalId = approvalId
            },
            _fran, calledOff.Token);

        await calledOff.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(asked);
        _sut.IsApprovalPending(approvalId).ShouldBeFalse();
        _hubSender.Verify(sender => sender.SendAsync(
                "OnApprovalResolved",
                new ApprovalResolvedNotification("topic1", approvalId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Offered to an account, the question goes to that person's connections wherever they are, and
    // is theirs to answer from any of them.
    [Fact]
    public async Task OfferApprovalAsync_GoesToTheAskersConnections_AndTheirAnswerIsTheOffersAnswer()
    {
        _connections.Opened("fran");
        var approvalId = Guid.NewGuid().ToString("N");

        var offered = _sut.OfferApprovalAsync(Offer(approvalId));

        _hubSender.Verify(sender => sender.SendToGroupAsync(
                "account:fran", "OnApprovalOffered",
                It.Is<ApprovalOfferedNotification>(n => n.Request.ApprovalId == approvalId && n.Request.AskedBy == "fran"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _sut.OfferedTo("fran").ShouldHaveSingleItem().ApprovalId.ShouldBe(approvalId);
        _sut.OfferedTo("laura").ShouldBeEmpty();
        _push.VerifyNoOtherCalls();

        await _sut.RespondToApprovalAsync(approvalId, "approvedandremember");

        (await offered).ShouldBe("approvedandremember");
    }

    // With no chat client open anywhere, the person's devices are told instead, and the question
    // waits for them to open it.
    [Fact]
    public void OfferApprovalAsync_ToSomeoneWithNoConnectionOpen_IsPushedToTheirDevices()
    {
        var approvalId = Guid.NewGuid().ToString("N");

        _ = _sut.OfferApprovalAsync(Offer(approvalId));

        _push.Verify(push => push.SendToPersonAsync(
                "fran", SpaceConfig.DefaultSlug, It.IsAny<string>(), It.Is<string>(body => body.Contains("buy")),
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _sut.OfferedTo("fran").ShouldHaveSingleItem().ApprovalId.ShouldBe(approvalId);
    }

    [Fact]
    public async Task SettleApprovalAsync_AnOfferAnsweredElsewhere_IsNoAnswerHere_AndSaysSoToItsAsker()
    {
        _connections.Opened("fran");
        var approvalId = Guid.NewGuid().ToString("N");
        var offered = _sut.OfferApprovalAsync(Offer(approvalId));

        await _sut.SettleApprovalAsync(approvalId, ToolApprovalResult.Rejected);

        (await offered).ShouldBe(ChannelProtocol.NoAnswer);
        _sut.OfferedTo("fran").ShouldBeEmpty();
        _hubSender.Verify(sender => sender.SendToGroupAsync(
                "account:fran", "OnApprovalResolved",
                new ApprovalResolvedNotification("", approvalId, null, true),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestApprovalAsync_ApprovalGranted_ReturnsApproved()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "mcp__server__tool", new Dictionary<string, object?> { ["key"] = "val" })];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1");
        pending.ShouldNotBeNull();
        _sut.IsApprovalPending(pending.ApprovalId).ShouldBeTrue();

        await _sut.RespondToApprovalAsync(pending.ApprovalId, "approved");

        var result = await approvalTask;
        result.ShouldBe("approved");
    }

    // The id is all a browser sends to answer an approval, so it must not be guessable: a full
    // 128-bit GUID, not the eight hex characters a caller could walk through.
    [Fact]
    public void RequestApprovalAsync_MintsAFullGuidAsTheApprovalId()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "mcp__server__tool", new Dictionary<string, object?>())];

        _ = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1").ShouldNotBeNull();
        pending.ApprovalId.Length.ShouldBe(32);
        Guid.TryParseExact(pending.ApprovalId, "N", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task RequestApprovalAsync_Rejected_ReturnsRejected()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>())];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1");
        await _sut.RespondToApprovalAsync(pending!.ApprovalId, "rejected");

        var result = await approvalTask;
        result.ShouldBe("rejected");
    }

    // ADR-0035: a question nobody can see must not hold the turn. A request-mode approval with
    // no live session used to register under a value matching nothing and await an answer no
    // deletion could release; it is answered at once as not answered here, which ends it unless
    // its asker is offered it somewhere else.
    [Fact]
    public async Task RequestApprovalAsync_NoLiveSession_IsNoAnswerAtOnce()
    {
        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>())];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        // Bounded by the test, not the code: today this never completes.
        var completed = await Task.WhenAny(approvalTask, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.ShouldBeSameAs(approvalTask);
        (await approvalTask).ShouldBe(ChannelProtocol.NoAnswer);
    }

    [Fact]
    public async Task RequestApprovalAsync_NoLiveSession_LeavesNoPendingApprovalBehind()
    {
        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>())];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });
        var completed = await Task.WhenAny(approvalTask, Task.Delay(TimeSpan.FromSeconds(5)));

        // Nothing to leak and nothing for the cancellation sweep to miss — under the old
        // coercion the request registered itself under the conversation spelling.
        completed.ShouldBeSameAs(approvalTask);
        _sut.GetPendingApprovalForTopic("100:200").ShouldBeNull();
    }

    // ADR-0035: an auto-approval notification into a conversation nobody has open renders
    // nothing, costs nothing and blocks nothing — and never warns.
    [Fact]
    public async Task NotifyAutoApprovedAsync_NoLiveSession_ReturnsWithoutWritingOrWarning()
    {
        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>())];

        await _sut.NotifyAutoApprovedAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Notify, Requests = requests });

        _streamLogger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
        _streamService.GetStreamState("100:200").ShouldBeNull();
    }

    [Fact]
    public async Task CancelPendingApprovalsForTopicAsync_RejectsAll()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>())];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        await _sut.CancelPendingApprovalsForTopicAsync("topic1");

        var result = await approvalTask;
        result.ShouldBe("rejected");
    }

    // A cancelled turn ends the prompt it raised, so the browsers showing it are told the same way
    // an answer tells them. Without the push the modal stays up over a stream that is already gone,
    // and the only way out of it is answering for a tool call nobody is waiting on any more.
    [Fact]
    public async Task CancelPendingApprovalsForTopicAsync_TakesThePromptOffEveryBrowser()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>())];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1").ShouldNotBeNull();

        await _sut.CancelPendingApprovalsForTopicAsync("topic1");
        await approvalTask;

        _hubSender.Verify(
            sender => sender.SendAsync(
                "OnApprovalResolved",
                new ApprovalResolvedNotification("topic1", pending.ApprovalId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAutoApprovedAsync_WritesToStream()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        var (channel, _) = _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var reader = channel.Subscribe();

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "search", new Dictionary<string, object?> { ["q"] = "test" })];

        await _sut.NotifyAutoApprovedAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Notify, Requests = requests });

        var msg = await reader.ReadAsync();
        msg.ToolCalls.ShouldNotBeNullOrEmpty();
        msg.MessageId.ShouldBe("msg-1");
    }

    // The topic's stream is the one route a tool call reaches the browser by. It is buffered, so a
    // reload replays it, and it arrives in order with the rest of the reply. A hub push beside it
    // is a second delivery of the same text that no client can use: a browser applies a pushed
    // tool call only to a reply it is already reading, which is a browser reading the stream.
    [Fact]
    public async Task NotifyAutoApprovedAsync_PushesNothingBesideTheStream()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "search", new Dictionary<string, object?> { ["q"] = "test" })];

        await _sut.NotifyAutoApprovedAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Notify, Requests = requests });

        _hubSender.VerifyNoOtherCalls();
    }

    // The approved call goes into the stream under the message it belongs to, exactly as an
    // auto-approved one does. Unlabelled, it lands on whatever the reply happens to be writing.
    [Fact]
    public async Task RequestApprovalAsync_Approved_WritesTheToolCallsUnderTheirOwnMessage()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        var (channel, _) = _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var reader = channel.Subscribe();

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "mcp__server__search", new Dictionary<string, object?>())];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1");
        await _sut.RespondToApprovalAsync(pending!.ApprovalId, "approved");
        await approvalTask;

        var request = await reader.ReadAsync();
        request.ApprovalRequest.ShouldNotBeNull();

        var toolCalls = await reader.ReadAsync();
        toolCalls.ToolCalls.ShouldNotBeNullOrEmpty();
        toolCalls.MessageId.ShouldBe("msg-1");
    }

    // The push exists to take the prompt off every browser showing it. The tool calls it used to
    // carry were the stream's copy a second time.
    [Fact]
    public async Task RespondToApprovalAsync_Approved_PushesOnlyTheDismissal()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        IReadOnlyList<ToolApprovalRequest> requests =
            [new ToolApprovalRequest("msg-1", "mcp__server__search", new Dictionary<string, object?>())];

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = ApprovalMode.Request, Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1");
        await _sut.RespondToApprovalAsync(pending!.ApprovalId, "approved");
        await approvalTask;

        _hubSender.Verify(
            sender => sender.SendAsync(
                "OnApprovalResolved",
                new ApprovalResolvedNotification("topic1", pending.ApprovalId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void GetPendingApprovalForTopic_NoPending_ReturnsNull()
    {
        _sut.GetPendingApprovalForTopic("nonexistent").ShouldBeNull();
    }

    public void Dispose() => _streamService.Dispose();
}