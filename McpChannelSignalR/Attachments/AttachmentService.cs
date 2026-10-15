using Domain.Conversations;
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
    public AttachmentLimits Limits { get; } = settings.Limits;

    public UploadTicket MintUpload(string topicId, ConversationIdentity conversation, string? spaceSlug) =>
        tickets.MintUpload(topicId, conversation, spaceSlug);

    public AttachmentTickets.UploadScope? ResolveUploadScope(string token, string topicId) =>
        tickets.ResolveUpload(token, topicId);

    public DictationTicket MintDictation(string spaceSlug) => tickets.MintDictation(spaceSlug);

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

    // A review copy's file, kept under the reference the shared history names it by, in the space
    // the conversation's topic is in. The same rules an upload meets, so the store only ever holds
    // what this chat can show. Null when kept; otherwise why not.
    public async Task<string?> KeepCopyAsync(
        string conversationId, string? spaceSlug, string attachmentId, string fileName, string mediaType,
        byte[] bytes, CancellationToken ct)
    {
        if (AttachmentRefusals.For(fileName, mediaType, bytes.LongLength, Limits) is { } refusal)
        {
            return refusal;
        }

        return await store.SaveCopyAsync(attachmentId, conversationId, spaceSlug ?? SpaceDefault, fileName, mediaType, bytes, ct)
            ? null
            : $"Attachment '{attachmentId}' cannot be kept here: its id names no file this store could hold for " +
              $"conversation '{conversationId}', or one it already holds.";
    }

    public Task<byte[]?> ReadBytesAsync(string attachmentId, CancellationToken ct) =>
        store.ReadBytesAsync(attachmentId, ct);

    public void DeleteConversation(string conversationId) => store.DeleteConversation(conversationId);

    public const string SpaceDefault = "default";
}