using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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
        IServiceProvider services,
        [Description("The question's own id, by which it is settled")] string? approvalId = null,
        CancellationToken cancellationToken = default) =>
        // The asking turn, from the context the agent stamps on this hop.
        RunAsync(conversationId, mode, requests, context.Turn, services,
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
        var answerers = await AnswerersOfAsync(turn, chatId, access);
        var (id, resultTask) = router.RegisterApproval(_approvalTimeout, answerers, cancellationToken, p.ApprovalId);

        var sent = await botClient.SendMessage(
            chatId,
            ApprovalCallbackRouter.FormatApprovalMessage(p.Requests),
            ParseMode.Html,
            replyMarkup: ApprovalCallbackRouter.CreateApprovalKeyboard(id),
            messageThreadId: threadId,
            cancellationToken: CancellationToken.None);
        router.Shown(id, botClient, chatId, sent.MessageId);

        // Asked in a group, the question is also put in its asker's private chat with the bot, where
        // a phone shows it to them alone; the same keyboard answers it from either.
        if (chatId < 0 && Principal.Of(turn)?.Account is { } asker)
        {
            try
            {
                await router.ShowInPrivateChatAsync(id, asker, p.Requests, botClient, access, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiRequestException)
            {
                // The group already shows it; a private chat the person never opened with this bot, or
                // an accounts service that could not say which it is, costs only the second copy.
            }
        }

        return await resultTask;
    }

    // Who may answer, read from the turn's context on this hop — whose principal is the call token's,
    // the one person the agent signed this call for, never a name the hop's own _meta claimed. A turn
    // acting for an account — a person's own message on any channel, or a fire whose delegation
    // verified as whoever wrote it — is that account's alone (TelegramAccess.AnswersFor). A fire that
    // names nobody was started by nobody's message, so any linked member of the household whose chat
    // it is: the person's own, for a private chat, or the one the group is bound to. Any other turn
    // naming nobody — no context, a call signed for nobody, a chat no household holds — is answered by
    // nobody: every tap is refused and the approval times out as denied.
    private static async Task<ApprovalAnswerer> AnswerersOfAsync(
        ConversationContext? turn, long chatId, TelegramAccess access)
    {
        if (Principal.Of(turn)?.Account is { } account)
        {
            return access.AnswersFor(account);
        }

        if (turn?.OriginKind is null || await access.ChatTenantAsync(chatId, CancellationToken.None) is not { } household)
        {
            return (_, _) => Task.FromResult(false);
        }

        return async (tapper, ct) => (await access.PersonAsync(tapper, ct))?.Account.TenantId == household;
    }
}