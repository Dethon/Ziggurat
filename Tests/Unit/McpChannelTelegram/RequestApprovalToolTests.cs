using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
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
        _harness.BotRegistry.RegisterChatAgent(100, TelegramPollingHarness.AgentId);

        _services = new ServiceCollection()
            .AddSingleton(_harness.BotRegistry)
            .AddSingleton(_harness.CallbackRouter)
            .AddSingleton(_harness.Settings)
            .BuildServiceProvider();
    }

    private static ConversationContext Turn(string userId, MessageOriginKind? originKind) =>
        new(TelegramPollingHarness.AgentId, "100:100", userId, new ReplyTarget("telegram", "100:100"), OriginKind: originKind);

    [Fact]
    public async Task McpRun_NotifyMode_SendsAutoApprovedMessage()
    {
        IReadOnlyList<ToolApprovalRequest> requests = [new ToolApprovalRequest(null, "mcp__server__search", new Dictionary<string, object?> { ["q"] = "test" })];

        var result = await RequestApprovalTool.RunAsync("100:100", ApprovalMode.Notify, requests, null, _services);

        result.ShouldBe("notified");
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text.Contains("Auto-approved") && r.Text.Contains("search")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_NotifyMode_MultipleTools_ListsAllNames()
    {
        IReadOnlyList<ToolApprovalRequest> requests =
        [
            new ToolApprovalRequest(null, "mcp__a__search", new Dictionary<string, object?>()),
            new ToolApprovalRequest(null, "mcp__b__write", new Dictionary<string, object?>())
        ];

        var result = await RequestApprovalTool.RunAsync("100:100", ApprovalMode.Notify, requests, null, _services);

        result.ShouldBe("notified");
        _harness.BotClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text.Contains("search") && r.Text.Contains("write")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A fire's turn was started by nobody's message, so whoever is on the allowlist may answer it.
    [Fact]
    public async Task McpRun_RequestModeForAFire_AnyAllowlistedTapAnswers()
    {
        var turn = Turn("alice", MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, turn, _services);
        await _harness.TapAsync(fromUserId: 2, "tool_approve");

        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved");
    }

    // Nobody outside the allowlist answers a fire's approval either.
    [Fact]
    public async Task McpRun_RequestModeForAFire_ATapOffTheAllowlistLeavesItPending()
    {
        var turn = Turn("alice", MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, turn, _services);
        await _harness.TapAsync(fromUserId: 99, "tool_approve");

        await Eventually.Settle();
        approval.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task McpRun_UnknownChat_ThrowsInvalidOperation()
    {
        IReadOnlyList<ToolApprovalRequest> requests = [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

        await Should.ThrowAsync<InvalidOperationException>(
            () => RequestApprovalTool.RunAsync("999:999", ApprovalMode.Notify, requests, null, _services));
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

        var message = RequestApprovalTool.FormatApprovalMessage([request]);

        message.ShouldEndWith("<i>Why you're asked: It would delete or change something that's already there.</i>");
    }

    [Fact]
    public void ApprovalMessage_WithoutScreen_IsUnchanged()
    {
        var request = new ToolApprovalRequest(null, "mcp__server__tool", new Dictionary<string, object?> { ["q"] = "x" });

        RequestApprovalTool.FormatApprovalMessage([request]).ShouldBe(
            $"<b>🔧 Approval Required:</b> <code>tool</code>{Environment.NewLine}<blockquote expandable><i>q:</i> x</blockquote>");
    }

    public void Dispose() => _harness.Dispose();
}