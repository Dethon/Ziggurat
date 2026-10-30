using System.ComponentModel;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval from user or notify about auto-approved tools")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        RequestContext<CallToolRequestParams> context,
        IServiceProvider services,
        [Description("The question's own id, by which it is settled")] string? approvalId = null,
        CancellationToken cancellationToken = default)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests,
            ApprovalId = approvalId
        };

        var approvalService = services.GetRequiredService<IApprovalService>();

        if (p.Mode == ApprovalMode.Notify)
        {
            await approvalService.NotifyAutoApprovedAsync(p);
            return "notified";
        }

        // Whose question it is, from the turn the agent stamps on this hop: only that account answers.
        var asker = Principal.Of(ConversationScope.Parse(context.Params?.Meta))?.Account;
        return await approvalService.RequestApprovalAsync(p, asker, cancellationToken);
    }
}