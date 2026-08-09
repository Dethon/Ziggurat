using Domain.DTOs.WebChat;
using McpChannelSignalR.Settings;

namespace McpChannelSignalR.Attachments;

// What the live connection can do with the upload store, in one place: hand out permission to
// put bytes in, hand out permission to take them out again, and drop a conversation's files when
// its topic goes.
public sealed class AttachmentService(
    AttachmentSettings settings,
    AttachmentTickets tickets,
    AttachmentStore store,
    ILogger<AttachmentService> logger)
{
    public AttachmentLimits Limits { get; } = new(
        settings.MaxBytesPerFile, settings.MaxFilesPerMessage, settings.AllowedMediaTypes);

    public UploadTicket MintUpload(string topicId, string conversationId, string? spaceSlug) =>
        tickets.MintUpload(topicId, conversationId, spaceSlug);

    public AttachmentTickets.UploadScope? ResolveUploadScope(string token, string topicId) =>
        tickets.ResolveUpload(token, topicId);

    // Null for both refusals a caller can do nothing about: the file is gone, or it belongs to
    // another space. One upload store serves every space, so the space check is what stops a
    // minted URL from being a read over someone else's conversation.
    public AttachmentDownload? MintDownload(string attachmentId, string requesterSpace)
    {
        var record = store.Find(attachmentId);
        if (record is null)
        {
            return null;
        }

        if ((record.SpaceSlug ?? SpaceDefault) != requesterSpace)
        {
            logger.LogWarning(
                "A download of attachment {AttachmentId} was asked for from space {RequesterSpace} " +
                "but the file belongs to {OwnerSpace}; refusing",
                attachmentId, requesterSpace, record.SpaceSlug);
            return null;
        }

        var ticket = tickets.MintDownload(attachmentId);
        return new AttachmentDownload(
            $"{AttachmentEndpointPaths.Attachments}/{attachmentId}"
            + $"?{AttachmentEndpointPaths.TicketQueryParameter}={ticket.Token}",
            ticket.ExpiresAt);
    }

    public Task<byte[]?> ReadBytesAsync(string attachmentId, CancellationToken ct) =>
        store.ReadBytesAsync(attachmentId, ct);

    public void DeleteConversation(string conversationId) => store.DeleteConversation(conversationId);

    public const string SpaceDefault = "default";
}