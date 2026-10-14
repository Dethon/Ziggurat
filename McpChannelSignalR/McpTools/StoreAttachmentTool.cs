using System.ComponentModel;
using Domain.DTOs.Channel;
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
        // space, but a topic moved elsewhere keeps its files beside it.
        var space = services.GetRequiredService<SessionService>().GetSessionByConversationId(conversationId)?.SpaceSlug;
        var refusal = await services.GetRequiredService<AttachmentService>().KeepCopyAsync(
            conversationId, space, attachmentId, fileName, mediaType, bytes, cancellationToken);
        return refusal is null ? "ok" : throw new McpException(refusal);
    }
}