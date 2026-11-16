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
    // every household): two households' servers of one name each keep their own. A source with one
    // connection per channel id has no other to ask.
    Task<byte[]?> FetchAsync(string channelId, string? householdId, string attachmentId, CancellationToken ct) =>
        FetchAsync(channelId, attachmentId, ct);
}