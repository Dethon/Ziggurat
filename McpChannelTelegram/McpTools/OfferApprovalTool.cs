using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

// A question another channel asked — nabu aloud, the chat client — offered to its asker in their
// private chat with the bot, whose id is their own Telegram id, by the bot of the agent that asked.
// It waits for as long as the question does: the agent settles it or calls it off.
[McpServerToolType]
public sealed class OfferApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.OfferApprovalTool)]
    [Description("Offer a question another channel asked to its asker in their Telegram private chat")]
    public static Task<string> McpRun(
        [Description("The question's own id")] string approvalId,
        [Description("The conversation the question is about")] string conversationId,
        [Description("The account whose question it is")] Account asker,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services,
        [Description("The agent the turn ran on")] string? agentId = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(new OfferApprovalParams
        {
            ApprovalId = approvalId,
            ConversationId = conversationId,
            Asker = asker,
            AgentId = agentId,
            Requests = requests
        }, services, cancellationToken);

    public static async Task<string> RunAsync(
        OfferApprovalParams p, IServiceProvider services, CancellationToken cancellationToken)
    {
        var router = services.GetRequiredService<ApprovalCallbackRouter>();
        var access = services.GetRequiredService<TelegramAccess>();
        var botClient = p.AgentId is null ? null : services.GetRequiredService<BotRegistry>().FindBotForAgent(p.AgentId);
        if (botClient is null || await access.PrivateChatOfAsync(p.Asker, cancellationToken) is not { } privateChat)
        {
            return ChannelProtocol.NoAnswer;
        }

        var (approvalId, resultTask) = router.RegisterApproval(
            Timeout.InfiniteTimeSpan, RequestApprovalTool.AccountAnswers(p.Asker, access), cancellationToken,
            p.ApprovalId);
        try
        {
            var sent = await botClient.SendMessage(
                privateChat,
                RequestApprovalTool.FormatApprovalMessage(p.Requests),
                ParseMode.Html,
                replyMarkup: ApprovalCallbackRouter.CreateApprovalKeyboard(approvalId),
                cancellationToken: CancellationToken.None);
            router.Shown(approvalId, botClient, privateChat, sent.MessageId);
        }
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException)
        {
            // A person who never opened a chat with this agent's bot cannot be written to first, and
            // a question that reached nobody is withdrawn as one answered elsewhere would be.
            await router.SettleAsync(approvalId, CancellationToken.None);
            return ChannelProtocol.NoAnswer;
        }

        return await resultTask;
    }
}