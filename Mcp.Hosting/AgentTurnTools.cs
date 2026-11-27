using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// What a channel server answers the agent with while somebody waits on one request for the whole reply
// (AgentTurns): nothing is shown to anybody chunk by chunk, so each chunk is collected for the turn it
// answers, and the reply is handed over when the turn ends. Nobody on such a channel can be asked to
// approve a call either: they are waiting on an answer, not watching a conversation, so a request
// reaching here is refused rather than granted unseen.
[McpServerToolType]
public sealed class AgentTurnTools(AgentTurns turns)
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Send a reply chunk to the request waiting on its turn")]
    public string SendReply(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        [Description("Key of the turn this reply answers")] string? turnKey = null,
        [Description("Whether the turn this reply answers was agent-initiated")] bool? agentInitiated = null)
    {
        turns.Receive(new SendReplyParams
        {
            ConversationId = conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            MessageId = messageId,
            TurnKey = turnKey,
            AgentInitiated = agentInitiated
        });
        return "ok";
    }

    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval — nobody waiting on this channel can be asked; requests are rejected")]
    public static string RequestApproval(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests)
        => mode == ApprovalMode.Notify ? "notified" : "rejected";
}

public static class AgentTurnExtensions
{
    // The turns a server waits on and the two tools their replies and approvals arrive by go together:
    // a reply tool without the table drops every reply, a table without the tool waits until timeout.
    public static IMcpServerBuilder AddAgentTurns(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<AgentTurns>();
        return builder.WithTools<AgentTurnTools>();
    }
}