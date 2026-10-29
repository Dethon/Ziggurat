using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// An approval raised inside a person's turn belongs to that person. The bot admits their message,
// the agent asks for approval within the turn it started, and only their tap counts — anyone else
// in the chat sees the keyboard and is told it isn't theirs.
public class TelegramBotServiceApprovalTests : IDisposable
{
    private static readonly IReadOnlyList<ToolApprovalRequest> ARequest =
        [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

    private readonly TelegramPollingHarness _harness = new();
    private readonly IServiceProvider _services;

    public TelegramBotServiceApprovalTests()
    {
        _services = new ServiceCollection()
            .AddSingleton(_harness.BotRegistry)
            .AddSingleton(_harness.CallbackRouter)
            .AddSingleton(_harness.Access)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task AnotherPersonsTap_LeavesTheApprovalPending_AndTheAskersTapResolvesIt()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage(
                "/delete it", username: "alice", userId: TelegramPollingHarness.AliceId)
        });
        await _harness.RunAsync();

        var approval = RequestApprovalTool.RunAsync(
            "100:100", ApprovalMode.Request, ARequest, PersonsTurn("alice"), _services);

        await _harness.TapAsync(TelegramPollingHarness.BobId, "tool_approve");
        await Eventually.Settle();
        approval.IsCompleted.ShouldBeFalse();
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text == "This approval isn't yours"),
            It.IsAny<CancellationToken>()), Times.Once);

        await _harness.TapAsync(TelegramPollingHarness.AliceId, "tool_approve");
        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved");
    }

    // A linked person with no username is the turn's sender by their id, and their tap still counts.
    [Fact]
    public async Task APersonWithNoUsername_AnswersTheirOwnApproval()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage(
                "/delete it", username: null, userId: TelegramPollingHarness.BobId)
        });
        await _harness.RunAsync();

        var sender = (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Sender;
        sender.ShouldBe($"{TelegramPollingHarness.BobId}");

        var approval = RequestApprovalTool.RunAsync(
            "100:100", ApprovalMode.Request, ARequest, PersonsTurn(sender), _services);
        await _harness.TapAsync(TelegramPollingHarness.BobId, "tool_reject");

        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("denied");
    }

    // A login wake continues the turn of the person who handed the login off, and carries them as
    // its principal: it is theirs to approve, not the whole household's as a schedule's fire is.
    [Fact]
    public async Task ALoginWakesApproval_IsTheAskersAlone()
    {
        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update
        {
            Id = 1,
            Message = TelegramPollingHarness.TextMessage(
                "/check my order", username: "alice", userId: TelegramPollingHarness.AliceId)
        });
        await _harness.RunAsync();

        var wake = PersonsTurn("alice") with
        {
            OriginKind = MessageOriginKind.Login,
            Principal = new Principal("telegram", "alice")
        };
        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, wake, _services);

        await _harness.TapAsync(TelegramPollingHarness.BobId, "tool_approve");
        await Eventually.Settle();
        approval.IsCompleted.ShouldBeFalse();

        await _harness.TapAsync(TelegramPollingHarness.AliceId, "tool_approve");
        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved");
    }

    // The context the agent stamps on a person's own message: no origin kind, and the sender the
    // bot emitted as the user id.
    private static ConversationContext PersonsTurn(string sender) =>
        new(TelegramPollingHarness.AgentId, "100:100", sender, new ReplyTarget("telegram", "100:100"));

    public void Dispose() => _harness.Dispose();
}