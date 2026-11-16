using System.ComponentModel;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

// A timer ringing for one person, shown on every chat client they have open and pushed to their
// devices. It waits for as long as the alert rings: their dismissal answers it, and the agent
// settles it or calls it off otherwise.
[McpServerToolType]
public sealed class OfferAlertTool
{
    [McpServerTool(Name = ChannelProtocol.OfferAlertTool)]
    [Description("Ring an alert on every chat client its person has open, until they dismiss it")]
    public static Task<string> McpRun(
        [Description("The alert's own id")] string alertId,
        [Description("The account it rings for")] Account owner,
        [Description("What the person is told")] string text,
        IServiceProvider services,
        [Description("Whether it is a timer or an alarm")] AnnounceKind kind = AnnounceKind.Timer,
        [Description("The agent of the turn that set it")] string? agentId = null,
        CancellationToken cancellationToken = default) =>
        services.GetRequiredService<AlertService>().OfferAlertAsync(
            new OfferAlertParams { AlertId = alertId, Owner = owner, Text = text, Kind = kind, AgentId = agentId },
            cancellationToken);
}