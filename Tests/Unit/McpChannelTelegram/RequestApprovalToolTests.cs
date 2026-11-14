using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot.Requests;

namespace Tests.Unit.McpChannelTelegram;

public class RequestApprovalToolTests : IDisposable
{
    private static readonly IReadOnlyList<ToolApprovalRequest> ARequest =
        [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

    private readonly TelegramPollingHarness _harness = new();
    private readonly IServiceProvider _services;

    public RequestApprovalToolTests()
    {
        _harness.BotRegistry.RegisterChat(100, _harness.Bot);

        _services = new ServiceCollection()
            .AddSingleton(_harness.BotRegistry)
            .AddSingleton(_harness.CallbackRouter)
            .AddSingleton(_harness.Access)
            .BuildServiceProvider();
    }

    private static ConversationContext Turn(string userId, MessageOriginKind? originKind) =>
        new(TelegramPollingHarness.AgentId, "100:100", userId, new ReplyTarget("telegram", "100:100"), OriginKind: originKind);

    // A tool that runs unasked is nothing the person has to act on, and a chat is no place for a
    // line per call: in pass 20 one reminder posted four "Auto-approved" messages ahead of its
    // one-line answer (user's decision, 2026-11-06). The notification is acknowledged and shown
    // nowhere.
    [Fact]
    public async Task McpRun_NotifyMode_SendsNothing()
    {
        IReadOnlyList<ToolApprovalRequest> requests =
        [
            new ToolApprovalRequest(null, "mcp__a__search", new Dictionary<string, object?> { ["q"] = "test" }),
            new ToolApprovalRequest(null, "mcp__b__write", new Dictionary<string, object?>())
        ];

        var result = await RequestApprovalTool.RunAsync("100:100", ApprovalMode.Notify, requests, null, _services);

        result.ShouldBe("notified");
        _harness.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task McpRun_RequestModeForAFire_AnyLinkedMemberOfTheChatsHouseholdAnswers()
    {
        _harness.Accounts.Link(100, "carol");
        var turn = Turn("alice", MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, turn, _services);
        await _harness.TapAsync(fromUserId: TelegramPollingHarness.BobId, "tool_approve");

        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved");
    }

    // Nobody unlinked answers a fire's approval, nor anybody of another household.
    [Theory]
    [InlineData(99)]
    [InlineData(7)]
    public async Task McpRun_RequestModeForAFire_ATapFromOutsideTheHouseholdLeavesItPending(long tapper)
    {
        _harness.Accounts.Link(100, "carol");
        _harness.Accounts.Link(7, "marta", household: "household-elsewhere");
        var turn = Turn("alice", MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, turn, _services);
        await _harness.TapAsync(tapper, "tool_approve");

        await Eventually.Settle();
        approval.IsCompleted.ShouldBeFalse();
    }

    // A group's fire is the household's the group is bound to.
    [Fact]
    public async Task McpRun_RequestModeForAFireInABoundGroup_AnyLinkedMemberAnswers()
    {
        _harness.BotRegistry.RegisterChat(TelegramPollingHarness.FamilyGroup, _harness.Bot);
        _harness.Accounts.Bind(TelegramPollingHarness.FamilyGroup);
        var conversation = $"{TelegramPollingHarness.FamilyGroup}:{TelegramPollingHarness.FamilyGroup}";
        var turn = new ConversationContext(TelegramPollingHarness.AgentId, conversation, "alice",
            new ReplyTarget("telegram", conversation), OriginKind: MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync(conversation, ApprovalMode.Request, ARequest, turn, _services);
        await _harness.TapAsync(fromUserId: TelegramPollingHarness.BobId, "tool_reject");

        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("denied");
    }

    // A chat no household holds — an unbound group, a stranger's private chat — has nobody to ask.
    [Fact]
    public async Task McpRun_RequestModeForAFireInAChatNoHouseholdHolds_NobodyAnswers()
    {
        var turn = Turn("alice", MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, turn, _services);
        await _harness.TapAsync(fromUserId: TelegramPollingHarness.BobId, "tool_approve");

        await Eventually.Settle();
        approval.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task McpRun_UnknownChat_ThrowsInvalidOperation()
    {
        IReadOnlyList<ToolApprovalRequest> requests = [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

        await Should.ThrowAsync<InvalidOperationException>(
            () => RequestApprovalTool.RunAsync("999:999", ApprovalMode.Request, requests, null, _services));
    }

    // A Telegram tool should never receive another channel's address; when it does, the error
    // names the value instead of an unguarded parse throwing whatever it throws.
    [Fact]
    public async Task McpRun_SomeOtherChannelsAddress_IsRefusedNamingTheValue()
    {
        IReadOnlyList<ToolApprovalRequest> requests = [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => RequestApprovalTool.RunAsync("kitchen-satellite", ApprovalMode.Notify, requests, null, _services));

        ex.Message.ShouldContain("kitchen-satellite");
        ex.Message.ShouldContain("conversation identity");
    }

    // The screen's reason is a line in the message above the keyboard; a request the screen had
    // no part in reads exactly as before.
    [Fact]
    public void ApprovalMessage_WithScreenCodes_SaysWhy()
    {
        var request = new ToolApprovalRequest(null, "domain__filesystem__exec", new Dictionary<string, object?> { ["command"] = "rm -rf ~" })
        {
            Screen = [ExecScreenCodes.Destructive]
        };

        var message = ApprovalCallbackRouter.FormatApprovalMessage([request]);

        message.ShouldEndWith("<i>Why you're asked: It would delete or change something that's already there.</i>");
    }

    // A group's copy is read by everyone in it, so its reason speaks of the person who asked: "logged
    // in as you" read to the group as being about each of them (pass 9's note, still so in pass 20).
    // The asker's private copy keeps "you".
    [Fact]
    public async Task McpRun_AQuestionAskedInAGroup_SaysWhyAsThePersonWhoAsked()
    {
        _harness.BotRegistry.RegisterChat(TelegramPollingHarness.FamilyGroup, _harness.Bot);
        IReadOnlyList<ToolApprovalRequest> requests =
        [
            new ToolApprovalRequest(null, "mcp__websearch__web_browse", new Dictionary<string, object?> { ["asMe"] = true })
            {
                Screen = [ExecScreenCodes.AsPerson]
            }
        ];
        var group = $"{TelegramPollingHarness.FamilyGroup}:{TelegramPollingHarness.FamilyGroup}";
        var turn = new ConversationContext(TelegramPollingHarness.AgentId, group, "alice", new ReplyTarget("telegram", group));

        var asked = RequestApprovalTool.RunAsync(group, ApprovalMode.Request, requests, turn, _services);

        await Eventually.Until(() => _harness.Sent.Any(m => m.ChatId.Identifier == TelegramPollingHarness.FamilyGroup), "the group is asked");
        _harness.Sent.Single(m => m.ChatId.Identifier == TelegramPollingHarness.FamilyGroup).Text
            .ShouldEndWith("<i>Why you're asked: It would browse this site logged in as the person who asked.</i>");
        asked.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void ApprovalMessage_ForItsAsker_SaysWhyAsThem()
    {
        var request = new ToolApprovalRequest(null, "mcp__websearch__web_browse", new Dictionary<string, object?>())
        {
            Screen = [ExecScreenCodes.AsPerson]
        };

        ApprovalCallbackRouter.FormatApprovalMessage([request])
            .ShouldEndWith("<i>Why you're asked: It would browse this site logged in as you.</i>");
    }

    [Fact]
    public void ApprovalMessage_WithoutScreen_IsUnchanged()
    {
        var request = new ToolApprovalRequest(null, "mcp__server__tool", new Dictionary<string, object?> { ["q"] = "x" });

        ApprovalCallbackRouter.FormatApprovalMessage([request]).ShouldBe(
            $"<b>🔧 Approval Required:</b> <code>tool</code>{Environment.NewLine}<blockquote expandable><i>q:</i> x</blockquote>");
    }

    public void Dispose() => _harness.Dispose();
}