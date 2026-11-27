using System.ComponentModel;
using Domain.Agents;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

// A review copy shows another channel's conversation here, and the history the two share names its
// files by that channel's references, which the browser can only reach through this store. So the
// agent hands over a copy of each under the same reference (ADR 0046). Hidden from the model like
// every other channel-protocol tool: it writes into the store every conversation shares.
[McpServerToolType]
public sealed class StoreAttachmentTool
{
    [McpServerTool(Name = ChannelProtocol.StoreAttachmentTool)]
    [Description("Keep a copy of another channel's attachment under its own reference id, for a review copy to show")]
    public static async Task<string> McpRun(
        [Description("Conversation the attachment belongs to")] string conversationId,
        [Description("Attachment reference id, kept as it is")] string attachmentId,
        [Description("File name")] string fileName,
        [Description("Media type")] string mediaType,
        [Description("The file's bytes, base64")] string content,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw new McpException("The content is not base64.");
        }

        // The topic's space, as an upload takes its ticket's: a review copy is minted in the default
        // space, but a topic moved elsewhere keeps its files beside it. Under the agent the topic is
        // held by, as an upload is: the open page's session says both, and with no page open (a fire, a
        // reply nobody watches, a tool's picture) the topic itself does, else it was the home's default.
        var session = services.GetRequiredService<SessionService>().GetSessionByConversationId(conversationId);
        var (agentId, space) = session is not null
            ? (session.AgentId, session.SpaceSlug)
            : await TopicOfAsync(services, conversationId, cancellationToken) is { } topic
                ? (topic.AgentId, topic.SpaceSlug)
                : (null, null);
        var refusal = await services.GetRequiredService<AttachmentService>().KeepCopyAsync(
            new AgentKey(conversationId, agentId), space, attachmentId, fileName, mediaType, bytes, cancellationToken);
        return refusal is null ? "ok" : throw new McpException(refusal);
    }

    // The topic over a conversation, asked of each registered agent as a download's check asks it.
    private static async Task<TopicMetadata?> TopicOfAsync(
        IServiceProvider services, string conversationId, CancellationToken ct)
    {
        if (ConversationIdentity.Parse(conversationId) is not { } conversation
            || services.GetService<IThreadStateStore>() is not { } threads
            || services.GetService<IAgentCatalog>() is not { } catalog)
        {
            return null;
        }

        foreach (var agent in catalog.GetAll() ?? [])
        {
            if (await threads.GetTopicByChatIdAndThreadIdAsync(agent.Id, conversation.ChatId, conversation.ThreadId, ct) is { } topic)
            {
                return topic;
            }
        }

        return null;
    }
}