using System.ComponentModel;
using Domain.DTOs.Channel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The agent's word that an alert this server raised ended on the channels — dismissed on one of
// them, or rung out — so the server's own stop no longer reports it as ringing. The one alert tool a
// raising server answers: it shows the alert nowhere, so it is never offered one.
[McpServerToolType]
public sealed class ChannelAlertSettleTool
{
    [McpServerTool(Name = ChannelProtocol.SettleAlertTool)]
    [Description("An alert this server raised ended on the person's channels, so it no longer rings")]
    public static string McpRun(
        [Description("The alert's own id")] string alertId,
        IServiceProvider services,
        [Description("Whether a person dismissed it, rather than it ringing out")] bool dismissed = false)
    {
        services.GetRequiredService<ChannelAlerts>().Settle(alertId);
        return "settled";
    }
}
