using System.ComponentModel;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class SettleAlertTool
{
    [McpServerTool(Name = ChannelProtocol.SettleAlertTool)]
    [Description("Tell every message still ringing an alert that it ended elsewhere")]
    public static Task<string> McpRun(
        [Description("The alert's own id")] string alertId,
        IServiceProvider services,
        [Description("Whether a person dismissed it, rather than it ringing out")] bool dismissed = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(alertId, dismissed, services, cancellationToken);

    public static async Task<string> RunAsync(
        string alertId, bool dismissed, IServiceProvider services, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ApprovalCallbackRouter>().SettleAlertAsync(alertId, dismissed, cancellationToken);
        return "settled";
    }
}