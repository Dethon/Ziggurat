using Domain.DTOs.FileSystem;
using JetBrains.Annotations;

namespace Domain.Contracts;

// Where the bytes of an image the model read rest between the tool call that read it and the send
// that puts it in front of the model. The agent owns this rather than a channel: nobody sent the
// file, the model asked for it, so there is no channel to fetch it back from.
//
// Keyed by conversation and tool call, because a tool-result message carries the call id — that is
// what lets the send find the image a given result was about, and what keeps two images read in
// one batch apart.
[PublicAPI]
public interface IReadImageStore
{
    Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct);

    Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct);

    Task DeleteAsync(string conversationId, string callId, CancellationToken ct);
}