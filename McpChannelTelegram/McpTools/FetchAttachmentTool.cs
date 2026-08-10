using System.ComponentModel;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;

namespace McpChannelTelegram.McpTools;

// Telegram is the store, so this is the whole of it: no upload store, no volume, no sweeper and
// no retention on this channel (ADR 0022). A reference is `<agentId>/<file id>`, and the bytes are
// asked of Telegram at the moment the agent wants them.
//
// Hidden from the model like every other channel-protocol tool.
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
        var logger = services.GetRequiredService<ILogger<FetchAttachmentTool>>();

        var separator = attachmentId.IndexOf('/');
        if (separator <= 0 || separator == attachmentId.Length - 1)
        {
            logger.LogWarning("Attachment reference {AttachmentId} does not name an agent and a file", attachmentId);
            return string.Empty;
        }

        var agentId = attachmentId[..separator];
        var fileId = attachmentId[(separator + 1)..];

        var botClient = services.GetRequiredService<BotRegistry>().FindBotForAgent(agentId);
        if (botClient is null)
        {
            logger.LogWarning("Attachment {AttachmentId} names agent {AgentId}, which has no bot here",
                attachmentId, agentId);
            return string.Empty;
        }

        try
        {
            var file = await botClient.GetFile(fileId, cancellationToken);
            if (file.FilePath is null)
            {
                return string.Empty;
            }

            using var bytes = new MemoryStream();
            await botClient.DownloadFile(file.FilePath, bytes, cancellationToken);
            return Convert.ToBase64String(bytes.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An empty answer rather than an error: hydration turns it into a placeholder naming
            // the file, so an unreachable file costs the turn its picture and not its answer.
            // Nothing here expires, so this is only ever a transient failure.
            logger.LogWarning(ex, "Could not fetch attachment {AttachmentId} from Telegram", attachmentId);
            return string.Empty;
        }
    }
}