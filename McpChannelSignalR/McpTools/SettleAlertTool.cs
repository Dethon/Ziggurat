using System.ComponentModel;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SettleAlertTool
{
    [McpServerTool(Name = ChannelProtocol.SettleAlertTool)]
    [Description("Take an alert that ended elsewhere off every chat client still showing it")]
    public static async Task<string> McpRun(
        [Description("The alert's own id")] string alertId,
        IServiceProvider services,
        [Description("Whether a person dismissed it, rather than it ringing out")] bool dismissed = false)
    {
        await services.GetRequiredService<AlertService>().SettleAlertAsync(alertId, dismissed);
        return "settled";
    }
}