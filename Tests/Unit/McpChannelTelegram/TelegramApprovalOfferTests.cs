using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot.Requests;

namespace Tests.Unit.McpChannelTelegram;

// An approval is its asker's on Telegram as everywhere: only the account whose turn asked it settles
// it, from any chat, and a question another channel asked is offered in their private chat with the
// bot — whose id is their own Telegram id — and told when it was answered elsewhere.
public class TelegramApprovalOfferTests : IDisposable
{
    private static readonly IReadOnlyList<ToolApprovalRequest> _buy =
        [new ToolApprovalRequest(null, "mcp__shop__buy", new Dictionary<string, object?>())];

    private static readonly Account _alice = FakeTelegramAccounts.AccountOf("alice").Account;

    private readonly TelegramPollingHarness _harness = new();
    private readonly IServiceProvider _services;

    public TelegramApprovalOfferTests()
    {
        _harness.BotRegistry.RegisterChatAgent(TelegramPollingHarness.FamilyGroup, TelegramPollingHarness.AgentId);
        _harness.Accounts.Bind(TelegramPollingHarness.FamilyGroup);
        _services = new ServiceCollection()
            .AddSingleton(_harness.BotRegistry)
            .AddSingleton(_harness.CallbackRouter)
            .AddSingleton(_harness.Access)
            .BuildServiceProvider();
    }

    private static string Group => $"{TelegramPollingHarness.FamilyGroup}:{TelegramPollingHarness.FamilyGroup}";

    private static ConversationContext TurnIn(string conversation, Account? account, MessageOriginKind? origin = null) =>
        new(TelegramPollingHarness.AgentId, conversation, "alice", new ReplyTarget("telegram", conversation),
            OriginKind: origin, Principal: new Principal("telegram", "alice", account));

    private Task<string> OfferAsync(string approvalId, Account asker) =>
        OfferApprovalTool.RunAsync(new OfferApprovalParams
        {
            ApprovalId = approvalId,
            ConversationId = "31:41",
            Asker = asker,
            AgentId = TelegramPollingHarness.AgentId,
            Requests = _buy
        }, _services, CancellationToken.None);

    // In a group the household shares, the member beside the asker sees the keyboard and is told it
    // is not theirs; only the asker's tap settles it.
    [Fact]
    public async Task AnAccountsApproval_AnotherLinkedMembersTapIsIgnored_AndTheAskersSettlesIt()
    {
        var approval = RequestApprovalTool.RunAsync(
            Group, ApprovalMode.Request, _buy, TurnIn(Group, _alice), _services);

        await _harness.TapAsync(TelegramPollingHarness.BobId, "tool_approve");
        await Eventually.Settle();
        approval.IsCompleted.ShouldBeFalse();
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text == "This approval isn't yours"),
            It.IsAny<CancellationToken>()), Times.Once);

        await _harness.TapAsync(TelegramPollingHarness.AliceId, "tool_always");
        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved_and_remember");
    }

    // A schedule's fire acts for whoever wrote it: its approval is the creator's alone, not the
    // whole household's as a fire nobody stamped is.
    [Fact]
    public async Task AFiresApproval_IsTheSchedulesCreatorsAlone()
    {
        var approval = RequestApprovalTool.RunAsync(
            Group, ApprovalMode.Request, _buy, TurnIn(Group, _alice, MessageOriginKind.Schedule), _services);

        await _harness.TapAsync(TelegramPollingHarness.BobId, "tool_approve");
        await Eventually.Settle();
        approval.IsCompleted.ShouldBeFalse();

        await _harness.TapAsync(TelegramPollingHarness.AliceId, "tool_reject");
        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("denied");
    }

    // Asked in a group, the question also reaches the asker's private chat, and a tap in either
    // answers it.
    [Fact]
    public async Task AnAccountsApprovalInAGroup_IsAlsoShownInTheAskersPrivateChat()
    {
        var approval = RequestApprovalTool.RunAsync(
            Group, ApprovalMode.Request, _buy, TurnIn(Group, _alice), _services);

        await Eventually.Until(() => _harness.Sent.Count(s => s.ReplyMarkup is not null) == 2, "both keyboards to be sent");
        _harness.Sent.Select(s => s.ChatId.Identifier).ShouldBe(
            [TelegramPollingHarness.FamilyGroup, TelegramPollingHarness.AliceId], ignoreOrder: true);
        await _harness.TapAsync(TelegramPollingHarness.AliceId, "tool_approve");
        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved");
    }

    // nabu asked aloud; the person taps on Telegram, in their private chat with the bot.
    [Fact]
    public async Task AnOfferedApproval_IsSentToTheAskersPrivateChat_AndAnsweredByTheirTapAlone()
    {
        var approvalId = Guid.NewGuid().ToString("N");

        var offered = OfferAsync(approvalId, _alice);

        await Eventually.Until(() => _harness.Sent.Count == 1, "the offer to be sent");
        _harness.Sent.ShouldHaveSingleItem().ChatId.Identifier.ShouldBe(TelegramPollingHarness.AliceId);
        await _harness.TapAsync(TelegramPollingHarness.BobId, "tool_approve");
        await Eventually.Settle();
        offered.IsCompleted.ShouldBeFalse();

        await _harness.TapAsync(TelegramPollingHarness.AliceId, "tool_approve");
        (await offered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved");
    }

    [Fact]
    public async Task AnOfferToSomeoneWithNoTelegram_IsNoAnswerAtOnce()
    {
        var offered = await OfferAsync(Guid.NewGuid().ToString("N"), new Account(FakeTelegramAccounts.Household, "user-nobody"));

        offered.ShouldBe(ChannelProtocol.NoAnswer);
        _harness.Sent.ShouldBeEmpty();
    }

    // Answered aloud first, the keyboard in the private chat says so and asks nothing more.
    [Fact]
    public async Task AnOfferAnsweredElsewhere_SaysSoAndIsNoAnswerHere()
    {
        var approvalId = Guid.NewGuid().ToString("N");
        var offered = OfferAsync(approvalId, _alice);
        await Eventually.Until(() => _harness.Sent.Count == 1, "the offer to be sent");

        await SettleApprovalTool.RunAsync(approvalId, ToolApprovalResult.Approved, _services, CancellationToken.None);

        (await offered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(ChannelProtocol.NoAnswer);
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<EditMessageTextRequest>(r => r.Text.Contains("Answered elsewhere")
                                               && r.ChatId.Identifier == TelegramPollingHarness.AliceId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose() => _harness.Dispose();
}