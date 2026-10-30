using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SettleApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.SettleApprovalTool)]
    [Description("Tell every card still showing a question that it was answered elsewhere")]
    public static async Task<string> McpRun(
        [Description("The question's own id")] string approvalId,
        [Description("The answer given elsewhere")] ToolApprovalResult result,
        IServiceProvider services)
    {
        await services.GetRequiredService<IApprovalService>().SettleApprovalAsync(approvalId, result);
        return "settled";
    }
}