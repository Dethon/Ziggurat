using System.ComponentModel;
using Domain.DTOs.Channel;
using McpServerTimers.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpServerTimers.McpTools;

// The agent's word that a timer's alert ended on the channels — dismissed on one of them, or rung
// out — so ./dismiss no longer reports it as ringing. The one alert tool this server answers: it
// raised the alert and shows it nowhere, so it is never offered one.
[McpServerToolType]
public sealed class SettleAlertTool
{
    [McpServerTool(Name = ChannelProtocol.SettleAlertTool)]
    [Description("A timer's alert ended on the person's channels, so it no longer rings")]
    public static string McpRun(
        [Description("The alert's own id")] string alertId,
        IServiceProvider services,
        [Description("Whether a person dismissed it, rather than it ringing out")] bool dismissed = false)
    {
        services.GetRequiredService<ChannelAlerts>().Settle(alertId);
        return "settled";
    }
}