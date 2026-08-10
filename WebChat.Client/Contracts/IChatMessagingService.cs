using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IChatMessagingService
{
    // The streaming calls answer with a HubResult rather than an enumerable a caller has to
    // iterate to find out anything: not live is a value here, not something discovered by
    // getting nothing out of an empty sequence. What "live" checks is the connection at the
    // moment the call was made — SignalR's StreamAsyncCore hands back its enumerable without a
    // round trip, so a caller still learns whether the server actually opened the stream only
    // once it starts consuming it, same as it always has for a live-but-empty stream.
    Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> SendMessageAsync(
        string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null,
        IReadOnlyList<AttachmentReference>? attachments = null);
    Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> ResumeStreamAsync(string topicId);
    Task<HubResult<StreamState>> GetStreamStateAsync(string topicId);
    Task<HubResult<Nothing>> CancelTopicAsync(string topicId);
    Task<HubResult<bool>> EnqueueMessageAsync(
        string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null,
        IReadOnlyList<AttachmentReference>? attachments = null);
}