using Domain.DTOs.Channel;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;

namespace WebChat.Client.Contracts;

// Sending a message and driving a resumed stream. Whether a topic has a reply in flight is
// TopicStreams' to answer, not this.
public interface IStreamingService
{
    // Attachments normally come from the composer, which this reads itself. They are passed in
    // only when the composer no longer holds them — a retry of a message already sent once.
    Task SendMessageAsync(
        StoredTopic topic,
        string message,
        string? correlationId = null,
        IReadOnlyList<AttachmentReference>? attachments = null);

    // Two moments, deliberately apart. Showing takes what the server said the reply had
    // written and puts it on screen; reading attaches to the wire, and that call does not
    // come back until the reply's next chunk does. A caller that did them as one would show
    // the reply only once the agent spoke again.
    bool TryShowResumedStream(StreamLease lease, ChatMessageModel streamingMessage, string? startMessageId);

    Task<bool> TryReadResumedStreamAsync(StreamLease lease, StoredTopic topic);
}