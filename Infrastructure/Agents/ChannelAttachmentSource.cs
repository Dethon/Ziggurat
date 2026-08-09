using Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// The agent's way to an attachment's bytes: ask the channel that took the file, the same way it
// asks that channel to send a reply. Nothing is mounted, so there is no other route (ADR 0021).
public sealed class ChannelAttachmentSource(
    IReadOnlyList<IChannelConnection> connections,
    ILogger<ChannelAttachmentSource>? logger = null) : IAttachmentSource
{
    public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct)
    {
        var connection = connections.FirstOrDefault(c => c.ChannelId == channelId);
        if (connection is null)
        {
            logger?.LogWarning(
                "Attachment {AttachmentId} names channel {ChannelId}, which this agent has no connection to",
                attachmentId, channelId);
            return Task.FromResult<byte[]?>(null);
        }

        return connection.FetchAttachmentAsync(attachmentId, ct);
    }
}