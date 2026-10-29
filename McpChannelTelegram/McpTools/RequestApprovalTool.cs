using System.ComponentModel;
using System.Text;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using ModelContextProtocol.Protocol;
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
    public static Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        RequestContext<CallToolRequestParams> context,
        IServiceProvider services) =>
        // The asking turn, from the context the agent stamps on this hop.
        RunAsync(conversationId, mode, requests, ConversationScope.Parse(context.Params?.Meta), services);

    public static async Task<string> RunAsync(
        string conversationId,
        ApprovalMode mode,
        IReadOnlyList<ToolApprovalRequest> requests,
        ConversationContext? turn,
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

        var answerers = AnswerersOf(turn, router, services.GetRequiredService<ChannelSettings>());
        var (approvalId, resultTask) = router.RegisterApproval(_approvalTimeout, answerers, CancellationToken.None);
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

    // Who may answer, read from the turn's context on this hop. A fire's turn was started by
    // nobody's message, so anyone on the allowlist. A person's turn names them by the sender the bot
    // emitted, which the bot recorded against their numeric id when it admitted the message, so
    // only that id. A turn with no context, or a sender this process never admitted (it restarted
    // mid-turn), names nobody: every tap is refused and the approval times out as denied.
    private static IReadOnlySet<long> AnswerersOf(
        ConversationContext? turn, ApprovalCallbackRouter router, ChannelSettings settings)
    {
        if (turn?.OriginKind is not null)
        {
            return settings.AllowedUserIds.ToHashSet();
        }

        return Principal.Of(turn) is { } asker && router.SenderId(asker.UserId) is { } askerId
            ? new HashSet<long> { askerId }
            : new HashSet<long>();
    }

    internal static string FormatApprovalMessage(IReadOnlyList<ToolApprovalRequest> requests)
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

        // Why the exec screen put this to the person, last so it sits right above the keyboard.
        var reason = ExecScreenReasons.English([.. requests.SelectMany(r => r.Screen ?? []).Distinct()]);
        if (reason is not null)
        {
            sb.AppendLine($"<i>Why you're asked: {HtmlEncode(reason)}</i>");
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