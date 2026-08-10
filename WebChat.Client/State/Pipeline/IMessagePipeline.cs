using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;

namespace WebChat.Client.State.Pipeline;

public interface IMessagePipeline
{
    string SubmitUserMessage(
        string topicId, string content, string? senderId,
        IReadOnlyList<AttachmentReference>? attachments = null);

    void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages);

    // Null when this topic's messages have never been loaded, which is a different thing from
    // a conversation that has none.
    IReadOnlyList<ChatMessageModel>? MessagesFor(string topicId);

    void ResumeFromBuffer(BufferResumeResult result, string topicId, string? currentMessageId);

    bool WasSentByThisClient(string? correlationId);

    PipelineSnapshot GetSnapshot(string topicId);
}