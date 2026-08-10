using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class ChatMessagingService(IChatLiveConnection liveConnection) : IChatMessagingService
{
    public Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> SendMessageAsync(string topicId, string message,
        string? correlationId = null, AgentConfigPatch? configPatch = null,
        IReadOnlyList<AttachmentReference>? attachments = null) =>
        liveConnection.StreamAsync<ChatStreamMessage>(
            "SendMessage", topicId, message, correlationId, configPatch, attachments);

    public Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> ResumeStreamAsync(string topicId) =>
        liveConnection.StreamAsync<ChatStreamMessage>("ResumeStream", topicId);

    public Task<HubResult<StreamState>> GetStreamStateAsync(string topicId) =>
        liveConnection.InvokeAsync<StreamState>("GetStreamState", topicId);

    public Task<HubResult<Nothing>> CancelTopicAsync(string topicId) =>
        liveConnection.InvokeAsync("CancelTopic", topicId);

    public Task<HubResult<bool>> EnqueueMessageAsync(
        string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null,
        IReadOnlyList<AttachmentReference>? attachments = null) =>
        liveConnection.InvokeAsync<bool>(
            "EnqueueMessage", topicId, message, correlationId, configPatch, attachments);
}