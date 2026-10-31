using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot.Exceptions;

namespace McpChannelTelegram.McpTools;

// A question another channel asked — nabu aloud, the chat client — offered to its asker in their
// private chat with the bot, whose id is their own Telegram id: by their household's bot of the agent
// that asked, else by the first of their household's other bots or the shared bot they opened a chat
// with. It waits for as long as the question does: the agent settles it or calls it off.
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
        var bots = services.GetRequiredService<BotRegistry>().BotsPreferring(p.Asker.TenantId, p.AgentId);
        var (approvalId, resultTask) = router.RegisterApproval(
            Timeout.InfiniteTimeSpan, access.AnswersFor(p.Asker), cancellationToken, p.ApprovalId);
        var shown = false;
        foreach (var botClient in bots)
        {
            try
            {
                shown = await router.ShowInPrivateChatAsync(approvalId, p.Asker, p.Requests, botClient, access, cancellationToken);
                break;
            }
            catch (ApiRequestException)
            {
                // A person who never opened a chat with this bot cannot be written to first; the next
                // may have one.
            }
            catch (HttpRequestException)
            {
                // An accounts service that could not say which chat is theirs shows it to nobody.
                break;
            }
        }

        if (!shown)
        {
            // A question that reached nobody is withdrawn as one answered elsewhere would be.
            await router.SettleAsync(approvalId, CancellationToken.None);
            return ChannelProtocol.NoAnswer;
        }

        return await resultTask;
    }
}