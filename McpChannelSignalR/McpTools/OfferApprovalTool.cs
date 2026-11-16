using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class OfferApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.OfferApprovalTool)]
    [Description("Offer a question another channel asked to its asker on every chat client they have open")]
    public static Task<string> McpRun(
        [Description("The question's own id")] string approvalId,
        [Description("The conversation the question is about")] string conversationId,
        [Description("The account whose question it is")] Account asker,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services,
        [Description("The agent the turn ran on")] string? agentId = null,
        CancellationToken cancellationToken = default) =>
        services.GetRequiredService<IApprovalService>().OfferApprovalAsync(
            new OfferApprovalParams
            {
                ApprovalId = approvalId,
                ConversationId = conversationId,
                Asker = asker,
                AgentId = agentId,
                Requests = requests
            },
            cancellationToken);
}