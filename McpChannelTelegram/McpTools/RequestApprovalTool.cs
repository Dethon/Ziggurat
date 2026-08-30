using System.ComponentModel;
using System.Text;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    private static readonly TimeSpan _approvalTimeout = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval from user or notify about auto-approved tools")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        var registry = services.GetRequiredService<BotRegistry>();
        var router = services.GetRequiredService<ApprovalCallbackRouter>();
        var (chatId, threadId) = TelegramConversation.Resolve(p.ConversationId);
        var botClient = registry.GetBotForChat(chatId)
                        ?? throw new InvalidOperationException($"No bot registered for chat {chatId}");

        if (p.Mode == ApprovalMode.Notify)
        {
            var toolNames = p.Requests.Select(r => r.ToolName.Split("__").Last());
            var message = $"✅ Auto-approved: {string.Join(", ", toolNames)}";

            await botClient.SendMessage(
                chatId,
                message,
                messageThreadId: threadId,
                cancellationToken: CancellationToken.None);

            return "notified";
        }

        var (approvalId, resultTask) = router.RegisterApproval(_approvalTimeout, CancellationToken.None);
        var keyboard = ApprovalCallbackRouter.CreateApprovalKeyboard(approvalId);

        var approvalMessage = FormatApprovalMessage(p.Requests);

        await botClient.SendMessage(
            chatId,
            approvalMessage,
            ParseMode.Html,
            replyMarkup: keyboard,
            messageThreadId: threadId,
            cancellationToken: CancellationToken.None);

        return await resultTask;
    }

    private static string FormatApprovalMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        var toolNames = string.Join(", ", requests.Select(r => r.ToolName.Split("__").Last()));
        sb.AppendLine($"<b>🔧 Approval Required:</b> <code>{HtmlEncode(toolNames)}</code>");

        foreach (var request in requests)
        {
            if (request.Arguments.Count == 0)
            {
                continue;
            }

            var details = new StringBuilder();
            foreach (var (key, value) in request.Arguments)
            {
                var formatted = value?.ToString()?.Replace("\n", " ") ?? "null";
                if (formatted.Length > 100)
                {
                    formatted = formatted[..100] + "...";
                }

                details.AppendLine($"<i>{HtmlEncode(key)}:</i> {HtmlEncode(formatted)}");
            }

            sb.AppendLine($"<blockquote expandable>{details.ToString().TrimEnd()}</blockquote>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string HtmlEncode(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}