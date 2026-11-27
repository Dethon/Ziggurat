using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Messages;

namespace WebChat.Client.State.Pipeline;

public sealed class MessagePipeline(
    IDispatcher dispatcher,
    MessagesStore messagesStore,
    TopicStreams topicStreams,
    ILogger<MessagePipeline> logger)
    : IMessagePipeline
{
    private readonly Dictionary<string, string> _pendingUserMessages = new();
    private readonly HashSet<string> _queuedUserMessages = [];
    private readonly Lock _lock = new();

    // A message sent while a reply streams is drawn by nobody now: the hub writes it on the stream as
    // its turn starts, carrying this id, and the page draws it there.
    public string QueueUserMessage()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            _queuedUserMessages.Add(correlationId);
        }

        return correlationId;
    }

    public bool WasQueuedByThisClient(string? correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return false;
        }

        lock (_lock)
        {
            return _queuedUserMessages.Contains(correlationId);
        }
    }

    public string SubmitUserMessage(
        string topicId, string content, string? senderId,
        IReadOnlyList<AttachmentReference>? attachments = null)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        lock (_lock)
        {
            _pendingUserMessages[correlationId] = topicId;
        }

        logger.LogDebug(
            "Pipeline.SubmitUserMessage: topic={TopicId}, correlationId={CorrelationId}, senderId={SenderId}",
            topicId, correlationId, senderId);

        dispatcher.Dispatch(new AddMessage(topicId, new ChatMessageModel
        {
            Role = "user",
            Content = content,
            CorrelationId = correlationId,
            SenderId = senderId,
            Timestamp = DateTimeOffset.UtcNow,
            Attachments = attachments
        }));

        return correlationId;
    }

    public void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages)
    {
        var chatMessages = messages.Select(h => h.ToChatMessageModel()).ToList();

        // MessagesLoaded records every message id it carries, so the finalized set follows.
        logger.LogDebug(
            "Pipeline.LoadHistory: topic={TopicId}, count={Count}",
            topicId, chatMessages.Count);

        dispatcher.Dispatch(new MessagesLoaded(topicId, chatMessages));
    }

    public IReadOnlyList<ChatMessageModel>? MessagesFor(string topicId) =>
        messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId);

    public void ResumeFromBuffer(BufferResumeResult result, string topicId)
    {
        logger.LogDebug(
            "Pipeline.ResumeFromBuffer: topic={TopicId}, mergedCount={MergedCount}, hasStreaming={HasStreaming}",
            topicId, result.MergedMessages.Count, result.StreamingMessage.HasContent);

        dispatcher.Dispatch(new MessagesLoaded(topicId, result.MergedMessages));

        if (!result.StreamingMessage.HasContent)
        {
            return;
        }

        // What the page already shows was stripped from the stream's copy (BufferRebuildUtility), so what is
        // left is what it has not drawn. The resumed stream was opened with this same message as its accumulator, so showing
        // it is the module's to do — this only says that no committed bubble owns it.
        topicStreams.PublishCurrent(topicId);
    }

    public bool WasSentByThisClient(string? correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return false;
        }

        lock (_lock)
        {
            return _pendingUserMessages.ContainsKey(correlationId);
        }
    }

    public PipelineSnapshot GetSnapshot(string topicId)
    {
        var finalizedCount = messagesStore.State.FinalizedMessageIdsByTopic
            .GetValueOrDefault(topicId)?.Count ?? 0;

        lock (_lock)
        {
            return new PipelineSnapshot(finalizedCount, _pendingUserMessages.Count);
        }
    }
}