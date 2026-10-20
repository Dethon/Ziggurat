using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerSugary.McpTools;

// Nobody on this channel can be asked: she is waiting for an estimate, not watching a conversation.
// The sugary agent's own tools are read-only and whitelisted, so a request reaching here is for
// something it was never meant to call, and it is refused rather than granted unseen.
[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval — the Sugary app has nobody to ask; requests are rejected")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests)
        => mode == ApprovalMode.Notify ? "notified" : "rejected";
}