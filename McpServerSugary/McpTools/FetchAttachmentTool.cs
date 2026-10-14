using System.ComponentModel;
using Domain.DTOs.Channel;
using McpServerSugary.Channel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpServerSugary.McpTools;

// The agent's way to a meal's photos, the same contract as WebChat's: hidden from the model like
// every channel-protocol tool, because one store holds every meal's photos (ADR-0021).
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
        var bytes = await services.GetRequiredService<UploadStore>().ReadBytesAsync(attachmentId, cancellationToken);

        // An empty answer rather than an error: a swept file is ordinary, and the agent turns it into
        // a placeholder naming the file.
        return bytes is null ? string.Empty : Convert.ToBase64String(bytes);
    }
}