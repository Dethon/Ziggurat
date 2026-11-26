using System.ComponentModel;
using Domain.DTOs.Channel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// The agent's way to the files a channel server's upload store holds, the same contract as WebChat's:
// hidden from the model like every channel-protocol tool, because one store holds every
// conversation's files (ADR-0021). A file stored for a household is handed over only on a call the
// agent signed for that household.
[McpServerToolType]
public sealed class UploadStoreFetchTool
{
    [McpServerTool(Name = ChannelProtocol.FetchAttachmentTool)]
    [Description("Fetch an attachment's bytes by naming its reference")]
    public static async Task<string> McpRun(
        RequestContext<CallToolRequestParams> context,
        [Description("Attachment reference id")] string attachmentId,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var bytes = await services.GetRequiredService<UploadStore>()
            .ReadBytesAsync(attachmentId, context.Household?.Id, cancellationToken);

        // An empty answer rather than an error: a swept file is ordinary, and the agent turns it into
        // a placeholder naming the file.
        return bytes is null ? string.Empty : Convert.ToBase64String(bytes);
    }
}
