using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
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

    // A message sent while the topic's reply streams: queued by the hub and drawn when its turn
    // starts, or sent and drawn at once when the reply turns out to be over.
    Task QueueMessageAsync(
        StoredTopic topic,
        string message,
        string? senderId,
        IReadOnlyList<AttachmentReference>? attachments = null);

    // Two moments, deliberately apart. Showing takes what the server said the reply had
    // written and puts it on screen; reading attaches to the wire, and that call does not
    // come back until the reply's next chunk does. A caller that did them as one would show
    // the reply only once the agent spoke again.
    // A reroll's turn, on a topic with no reply running: the message it ends on answered again.
    Task RerunAsync(
        StoredTopic topic,
        string message,
        string rerunOf,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference> attachments);

    // Continue, on a topic with no reply running: the cut-off last reply carried on.
    Task ContinueAsync(StoredTopic topic);

    bool TryShowResumedStream(StreamLease lease, ChatMessageModel streamingMessage, string? startMessageId);

    Task<bool> TryReadResumedStreamAsync(StreamLease lease, StoredTopic topic, StreamSeen? seen = null);

    // Reads the history and names what the page drew, putting in any turn no wire brought here.
    Task CatchUpWithHistoryAsync(StoredTopic topic);
}