using System.ComponentModel;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;

namespace McpChannelTelegram.McpTools;

// A timer ringing for one person, put in their private chat with the bot — whose id is their own
// Telegram id — with one Dismiss button. It waits for as long as the alert rings: their tap
// answers it, and the agent settles it or calls it off otherwise.
[McpServerToolType]
public sealed class OfferAlertTool
{
    [McpServerTool(Name = ChannelProtocol.OfferAlertTool)]
    [Description("Ring an alert in its person's Telegram private chat, until they dismiss it")]
    public static Task<string> McpRun(
        [Description("The alert's own id")] string alertId,
        [Description("The account it rings for")] Account owner,
        [Description("What the person is told")] string text,
        IServiceProvider services,
        [Description("Whether it is a timer or an alarm")] AnnounceKind kind = AnnounceKind.Timer,
        [Description("The agent of the turn that set it")] string? agentId = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(new OfferAlertParams { AlertId = alertId, Owner = owner, Text = text, Kind = kind, AgentId = agentId },
            services, cancellationToken);

    public static async Task<string> RunAsync(OfferAlertParams p, IServiceProvider services, CancellationToken cancellationToken)
    {
        var router = services.GetRequiredService<ApprovalCallbackRouter>();
        var access = services.GetRequiredService<TelegramAccess>();
        var (alertId, dismissed) = router.RegisterAlert(access.AnswersFor(p.Owner), p.AlertId, p.Text, cancellationToken);

        // By their household's bot of the agent that set it, which is the chat the person talks in,
        // else by the first other bot of their household's, or the shared bot, they opened a chat with.
        // A person with no Telegram linked is shown nothing by any bot.
        var bots = services.GetRequiredService<BotRegistry>().BotsPreferring(p.Owner.TenantId, p.AgentId);
        if (!await PrivateChatOffer.ShowAsync(bots,
                bot => router.ShowAlertInPrivateChatAsync(alertId, p.Owner, p.Text, bot, access, cancellationToken)))
        {
            // An alert that reached nobody is withdrawn as one that ended elsewhere would be.
            await router.SettleAlertAsync(alertId, dismissed: false, CancellationToken.None);
            return ChannelProtocol.NoAnswer;
        }

        return await dismissed == ChannelProtocol.AlertDismissed ? ChannelProtocol.AlertDismissed : ChannelProtocol.NoAnswer;
    }
}