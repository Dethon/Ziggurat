using JetBrains.Annotations;

namespace Domain.Contracts;

// Where an attachment's bytes come from when a turn is on its way to the model. The channel that
// took the file is the one that still holds it, so the caller names the channel as well as the
// reference; null means the bytes could not be had, which hydration turns into a placeholder.
[PublicAPI]
public interface IAttachmentSource
{
    Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct);
}