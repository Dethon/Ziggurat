using System.ComponentModel;
using Domain.DTOs.Channel;
using McpChannelSignalR.Attachments;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

// The only way into the upload store from the agent side. Hidden from the model like every other
// channel-protocol tool: one store serves every conversation, every user and every space, so a
// tool the model could call would be a read over other people's files (ADR 0021).
[McpServerToolType]
public sealed class FetchAttachmentTool
{
    [McpServerTool(Name = ChannelProtocol.FetchAttachmentTool)]
    [Description("Fetch an attachment's bytes by naming its reference")]
    public static async Task<string> McpRun(
        [Description("Attachment reference id")] string attachmentId,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var attachments = services.GetRequiredService<AttachmentService>();
        var bytes = await attachments.ReadBytesAsync(attachmentId, cancellationToken);

        // An empty answer rather than an error: a swept or deleted file is ordinary, and the
        // caller turns it into a placeholder naming the file.
        return bytes is null ? string.Empty : Convert.ToBase64String(bytes);
    }
}