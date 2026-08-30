using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

public sealed class StreamService(
    SessionService sessionService,
    IPushNotificationService pushNotificationService,
    ILogger<StreamService> logger) : IStreamService, IDisposable
{
    private readonly ConcurrentDictionary<string, BroadcastChannel<ChatStreamMessage>> _responseChannels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();
    private readonly ConcurrentDictionary<string, string> _currentPrompts = new();
    private readonly ConcurrentDictionary<string, string> _currentSenderIds = new();
    private readonly ConcurrentDictionary<string, int> _pendingPromptCounts = new();
    private readonly Lock _streamLock = new();
    private bool _disposed;

    public async Task WriteReplyAsync(SendReplyParams p)
    {
        var conversationId = p.ConversationId;
        var content = p.Content;
        var contentType = p.ContentType;
        var isComplete = p.IsComplete;
        var messageId = p.MessageId;

        // A turn with no live session delivers by persistence alone (ADR-0035): streaming is a
        // live-audience concern, the agent's history store already holds the reply, and the
        // browser catches up from it. The miss is designed, so it is quiet.
        var topicId = sessionService.GetTopicIdByConversationId(conversationId);
        if (topicId is null)
        {
            logger.LogDebug(
                "WriteReply: no live session for conversation {ConversationId}; delivering by persistence alone",
                conversationId);
            return;
        }

        // Use agent-provided messageId (from AgentResponseUpdate.MessageId) for proper bubble grouping
        var effectiveMessageId = messageId ?? topicId;

        var message = contentType switch
        {
            ReplyContentType.Text => new ChatStreamMessage { Content = content, MessageId = effectiveMessageId },
            ReplyContentType.Reasoning => new ChatStreamMessage { Reasoning = content, MessageId = effectiveMessageId },
            ReplyContentType.ToolCall => new ChatStreamMessage { ToolCalls = ToolCallFormatter.Format(content), MessageId = effectiveMessageId },
            ReplyContentType.Error => new ChatStreamMessage { Error = content, IsComplete = true },
            ReplyContentType.StreamComplete => new ChatStreamMessage { IsComplete = true, MessageId = effectiveMessageId },
            _ => new ChatStreamMessage { Content = content, MessageId = effectiveMessageId }
        };

        if (isComplete && contentType != ReplyContentType.Error && contentType != ReplyContentType.StreamComplete)
        {
            await WriteMessageAsync(topicId, message);
            var completeMessage = new ChatStreamMessage { IsComplete = true, MessageId = effectiveMessageId };
            await WriteMessageAsync(topicId, completeMessage);
            CompleteStream(topicId);
            return;
        }

        if (contentType is ReplyContentType.Error or ReplyContentType.StreamComplete)
        {
            await WriteMessageAsync(topicId, message);
            CompleteStream(topicId);
            return;
        }

        await WriteMessageAsync(topicId, message);
    }

    // Lock protects the compound check-then-act: without it, a concurrent CompleteStream
    // can remove a channel between TryGetValue and return, handing back a completed channel.
    public (BroadcastChannel<ChatStreamMessage> Channel, CancellationToken Token) GetOrCreateStream(
        string topicId,
        string currentPrompt,
        string? currentSenderId,
        CancellationToken parentToken)
    {
        lock (_streamLock)
        {
            if (_responseChannels.TryGetValue(topicId, out var existingChannel))
            {
                _currentPrompts[topicId] = currentPrompt;
                if (currentSenderId is not null)
                {
                    _currentSenderIds[topicId] = currentSenderId;
                }

                if (_cancellationTokens.TryGetValue(topicId, out var existingCts))
                {
                    return (existingChannel, existingCts.Token);
                }

                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                _cancellationTokens[topicId] = linkedCts;
                return (existingChannel, linkedCts.Token);
            }

            var broadcastChannel = new BroadcastChannel<ChatStreamMessage>();
            _responseChannels[topicId] = broadcastChannel;

            _streamBuffers.TryRemove(topicId, out _);

            _currentPrompts[topicId] = currentPrompt;
            if (currentSenderId is not null)
            {
                _currentSenderIds[topicId] = currentSenderId;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _cancellationTokens[topicId] = cts;

            return (broadcastChannel, cts.Token);
        }
    }

    public IAsyncEnumerable<ChatStreamMessage>? SubscribeToStream(
        string topicId,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        return !_responseChannels.TryGetValue(topicId, out var channel)
            ? null
            : channel.Subscribe().ReadAllAsync(cancellationToken);
    }

    public async Task WriteMessageAsync(
        string topicId,
        ChatStreamMessage message)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        if (!_responseChannels.TryGetValue(topicId, out var channel))
        {
            // No subscriber means no stream was ever created — the same designed miss as above,
            // one map further down.
            logger.LogDebug("WriteMessage: topicId {TopicId} not found in _responseChannels", topicId);
            return;
        }

        var buffer = _streamBuffers.GetOrAdd(topicId, _ => new StreamBuffer());
        buffer.Add(message);
        await channel.WriteAsync(message, CancellationToken.None);
    }

    private void CompleteStream(string topicId)
    {
        string? spaceSlug = null;
        string? topicName = null;

        lock (_streamLock)
        {
            // Decrement pending count — only tear down when the last agent finishes
            var remaining = _pendingPromptCounts.AddOrUpdate(topicId, 0, (_, count) => count - 1);
            if (remaining > 0)
            {
                return;
            }

            // Resolve space slug and topic name before cleanup
            if (sessionService.TryGetSession(topicId, out var session))
            {
                spaceSlug = session?.SpaceSlug;
                topicName = session?.TopicName;
            }

            if (_responseChannels.TryRemove(topicId, out var channel))
            {
                channel.Complete();
            }

            CleanupStreamState(topicId);
        }

        if (spaceSlug is not null)
        {
            _ = SendPushNotificationAsync(spaceSlug, topicName);
        }
    }

    private async Task SendPushNotificationAsync(string spaceSlug, string? topicName)
    {
        try
        {
            var url = $"/{spaceSlug}";
            await pushNotificationService.SendToSpaceAsync(
                spaceSlug,
                topicName ?? "New response",
                "The agent has finished responding",
                url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send push notification for space {SpaceSlug}", spaceSlug);
        }
    }

    public void CancelStream(string topicId)
    {
        lock (_streamLock)
        {
            if (_cancellationTokens.TryRemove(topicId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (_responseChannels.TryRemove(topicId, out var channel))
            {
                channel.Complete();
            }

            CleanupStreamState(topicId);
        }
    }

    public bool IsStreaming(string topicId)
    {
        lock (_streamLock)
        {
            return _responseChannels.ContainsKey(topicId);
        }
    }

    public bool TryIncrementPending(string topicId)
    {
        lock (_streamLock)
        {
            if (!_responseChannels.ContainsKey(topicId))
            {
                return false;
            }

            _pendingPromptCounts.AddOrUpdate(topicId, 1, (_, count) => count + 1);
            return true;
        }
    }

    public StreamState? GetStreamState(string topicId)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        var isProcessing = _responseChannels.ContainsKey(topicId);
        _currentPrompts.TryGetValue(topicId, out var currentPrompt);
        _currentSenderIds.TryGetValue(topicId, out var currentSenderId);

        if (!_streamBuffers.TryGetValue(topicId, out var buffer))
        {
            return isProcessing ? new StreamState(true, [], string.Empty, currentPrompt, currentSenderId) : null;
        }

        var messages = buffer.GetAll();
        var lastMessageId = messages.LastOrDefault()?.MessageId ?? string.Empty;

        return new StreamState(isProcessing, messages, lastMessageId, currentPrompt, currentSenderId);
    }

    private void CleanupStreamState(string topicId)
    {
        _streamBuffers.TryRemove(topicId, out _);
        _currentPrompts.TryRemove(topicId, out _);
        _currentSenderIds.TryRemove(topicId, out _);
        _pendingPromptCounts.TryRemove(topicId, out _);

        if (_cancellationTokens.TryRemove(topicId, out var cts))
        {
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var channel in _responseChannels.Values)
        {
            channel.Complete();
        }

        foreach (var cts in _cancellationTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}