using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Contracts;

// Where an attachment's bytes come from when a turn is on its way to the model. The channel that
// took the file is the one that still holds it, so the caller names the channel as well as the
// reference; null means the bytes could not be had, which hydration turns into a placeholder.
[PublicAPI]
public interface IAttachmentSource
{
    Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct);

    // The same, from the connection of the household that took the file (null for a channel serving
    // every household): two households' servers of one name each keep their own. Asked for the turn
    // being sent (null: none), whose identity the fetch is signed as, so a channel keeping a
    // household's files hands them only to that household's turns. A source with one connection per
    // channel id and nobody to sign for has no other to ask.
    Task<byte[]?> FetchAsync(
        string channelId, string? householdId, string attachmentId, ConversationContext? turn, CancellationToken ct) =>
        FetchAsync(channelId, attachmentId, ct);
}