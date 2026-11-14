using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class SettleApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.SettleApprovalTool)]
    [Description("Tell every keyboard still showing a question that it was answered elsewhere")]
    public static Task<string> McpRun(
        [Description("The question's own id")] string approvalId,
        [Description("The answer given elsewhere")] ToolApprovalResult result,
        IServiceProvider services,
        CancellationToken cancellationToken = default) =>
        RunAsync(approvalId, result, services, cancellationToken);

    public static async Task<string> RunAsync(
        string approvalId, ToolApprovalResult result, IServiceProvider services, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ApprovalCallbackRouter>().SettleAsync(approvalId, cancellationToken);
        return "settled";
    }
}