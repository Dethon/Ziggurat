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
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

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
        IServiceProvider services,
        [Description("The question's own id, by which it is settled")] string? approvalId = null,
        CancellationToken cancellationToken = default) =>
        // The asking turn, from the context the agent stamps on this hop.
        RunAsync(conversationId, mode, requests, ConversationScope.Parse(context.Params?.Meta), services,
            approvalId, cancellationToken);

    public static async Task<string> RunAsync(
        string conversationId,
        ApprovalMode mode,
        IReadOnlyList<ToolApprovalRequest> requests,
        ConversationContext? turn,
        IServiceProvider services,
        string? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests,
            ApprovalId = approvalId
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

        var access = services.GetRequiredService<TelegramAccess>();
        var answerers = await AnswerersOfAsync(turn, chatId, router, access);
        var (id, resultTask) = router.RegisterApproval(_approvalTimeout, answerers, cancellationToken, p.ApprovalId);
        var keyboard = ApprovalCallbackRouter.CreateApprovalKeyboard(id);

        var approvalMessage = FormatApprovalMessage(p.Requests);

        var sent = await botClient.SendMessage(
            chatId,
            approvalMessage,
            ParseMode.Html,
            replyMarkup: keyboard,
            messageThreadId: threadId,
            cancellationToken: CancellationToken.None);
        router.Shown(id, botClient, chatId, sent.MessageId);

        // Asked in a group, the question is also put in its asker's private chat with the bot, where
        // a phone shows it to them alone; the same keyboard answers it from either.
        if (chatId < 0 && Principal.Of(turn)?.Account is { } asker)
        {
            await ShowInPrivateChatAsync(id, asker, approvalMessage, keyboard, botClient, router, access);
        }

        return await resultTask;
    }

    private static async Task ShowInPrivateChatAsync(
        string approvalId, Account asker, string approvalMessage, InlineKeyboardMarkup keyboard,
        ITelegramBotClient botClient, ApprovalCallbackRouter router, TelegramAccess access)
    {
        try
        {
            if (await access.PrivateChatOfAsync(asker, CancellationToken.None) is not { } privateChat)
            {
                return;
            }

            var sent = await botClient.SendMessage(
                privateChat, approvalMessage, ParseMode.Html, replyMarkup: keyboard,
                cancellationToken: CancellationToken.None);
            router.Shown(approvalId, botClient, privateChat, sent.MessageId);
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiRequestException)
        {
            // The group already shows it; a private chat the person never opened with this bot, or
            // an accounts service that could not say which it is, costs only the second copy.
        }
    }

    // Who may answer, read from the turn's context on this hop. A turn acting for an account — a
    // person's own message on any channel, or a fire stamped with whoever wrote the schedule — is
    // that account's alone: a tap counts when the Telegram it came from is linked to it. A Telegram
    // person's turn from before accounts is theirs by the numeric id the bot recorded against the
    // sender it emitted when it admitted their message. A fire that names nobody was started by
    // nobody's message, so any linked member of the household whose chat it is: the person's own,
    // for a private chat, or the one the group is bound to. A turn with no context, a person this
    // process never admitted (it restarted mid-turn), or a chat no household holds names nobody:
    // every tap is refused and the approval times out as denied.
    private static async Task<Func<long, CancellationToken, Task<bool>>> AnswerersOfAsync(
        ConversationContext? turn, long chatId, ApprovalCallbackRouter router, TelegramAccess access)
    {
        if (Principal.Of(turn)?.Account is { } account)
        {
            return AccountAnswers(account, access);
        }

        if (Principal.Of(turn) is { ChannelId: TelegramAccess.Channel } asker)
        {
            var askerId = router.SenderId(asker.UserId);
            return (tapper, _) => Task.FromResult(tapper == askerId);
        }

        if (turn?.OriginKind is null || await access.ChatTenantAsync(chatId, CancellationToken.None) is not { } household)
        {
            return (_, _) => Task.FromResult(false);
        }

        return async (tapper, ct) => (await access.PersonAsync(tapper, ct))?.Account.TenantId == household;
    }

    internal static Func<long, CancellationToken, Task<bool>> AccountAnswers(Account asker, TelegramAccess access) =>
        async (tapper, ct) => (await access.PersonAsync(tapper, ct))?.Account == asker;

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