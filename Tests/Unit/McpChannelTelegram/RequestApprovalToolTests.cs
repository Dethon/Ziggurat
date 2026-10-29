using System.Collections.Concurrent;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Tests.Unit.McpChannelTelegram;

public class RequestApprovalToolTests
{
    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly ApprovalCallbackRouter _router = new();
    private readonly IServiceProvider _services;

    private static readonly IReadOnlyList<ToolApprovalRequest> ARequest =
        [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

    private readonly ConcurrentQueue<SendMessageRequest> _sent = new();

    public RequestApprovalToolTests()
    {
        _botClient
            .Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback((IRequest<Message> request, CancellationToken _) => _sent.Enqueue((SendMessageRequest)request))
            .ReturnsAsync(new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = 100, Type = ChatType.Private }
            });

        var botRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            ["jack"] = _botClient.Object
        });
        botRegistry.RegisterChatAgent(100, "jack");

        _services = new ServiceCollection()
            .AddSingleton(botRegistry)
            .AddSingleton(_router)
            .AddSingleton(new ChannelSettings
            {
                Bots = [new AgentBotConfig { AgentId = "jack", BotToken = "unused" }],
                AllowedUserIds = [1, 2]
            })
            .BuildServiceProvider();
    }

    private static ConversationContext Turn(string userId, MessageOriginKind? originKind) =>
        new("jack", "100:100", userId, new ReplyTarget("telegram", "100:100"), OriginKind: originKind);

    // Taps a button on the keyboard the approval was sent with, once it has been sent.
    private async Task TapAsync(long fromUserId, string button)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        InlineKeyboardMarkup? keyboard = null;
        while (keyboard is null && DateTime.UtcNow < deadline)
        {
            keyboard = _sent.Select(r => r.ReplyMarkup).OfType<InlineKeyboardMarkup>().LastOrDefault();
            if (keyboard is null)
            {
                await Task.Delay(10);
            }
        }

        var data = keyboard.ShouldNotBeNull().InlineKeyboard.First()
            .Single(b => b.CallbackData!.StartsWith(button + ":", StringComparison.Ordinal)).CallbackData;
        await _router.HandleCallbackQueryAsync(_botClient.Object, new CallbackQuery
        {
            Id = "cb-1",
            Data = data,
            From = new User { Id = fromUserId, IsBot = false, FirstName = "Someone" }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task McpRun_NotifyMode_SendsAutoApprovedMessage()
    {
        IReadOnlyList<ToolApprovalRequest> requests = [new ToolApprovalRequest(null, "mcp__server__search", new Dictionary<string, object?> { ["q"] = "test" })];

        var result = await RequestApprovalTool.RunAsync("100:100", ApprovalMode.Notify, requests, null, _services);

        result.ShouldBe("notified");
        _botClient.Verify(b => b.SendRequest(
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
        _botClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text.Contains("search") && r.Text.Contains("write")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // A fire's turn was started by nobody's message, so whoever is on the allowlist may answer it.
    [Fact]
    public async Task AFiresApproval_IsAnsweredByAnyAllowlistedTap()
    {
        var turn = Turn("alice", MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, turn, _services);
        await TapAsync(fromUserId: 2, "tool_approve");

        (await approval.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("approved");
    }

    // Nobody outside the allowlist answers a fire's approval either.
    [Fact]
    public async Task AFiresApproval_IsNotAnsweredBySomebodyOffTheAllowlist()
    {
        var turn = Turn("alice", MessageOriginKind.Schedule);

        var approval = RequestApprovalTool.RunAsync("100:100", ApprovalMode.Request, ARequest, turn, _services);
        await TapAsync(fromUserId: 99, "tool_approve");

        await Task.Delay(100);
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
}