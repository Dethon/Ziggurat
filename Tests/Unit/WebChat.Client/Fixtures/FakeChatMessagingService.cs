using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeChatMessagingService : IChatMessagingService
{
    private readonly Queue<ChatStreamMessage> _enqueuedMessages = new();
    private readonly Dictionary<string, StreamState> _streamStates = new();
    private readonly HashSet<string> _cancelledTopics = new();
    private bool _enqueueResult = true;
    private bool _blockUntilComplete;
    private readonly TaskCompletionSource _completionSource = new();
    private TaskCompletionSource? _sendAnswer;
    private Exception? _exceptionToThrow;

    public AgentConfigPatch? LastConfigPatch { get; private set; }

    public void SetExceptionToThrow(Exception? exception)
    {
        _exceptionToThrow = exception;
    }

    public void SetEnqueueResult(bool result) => _enqueueResult = result;

    public void SetBlockUntilComplete(bool block)
    {
        _blockUntilComplete = block;
    }

    public void UnblockCompletion()
    {
        _completionSource.TrySetResult();
    }

    public int StreamDelayMs { get; set; } = 0;

    public void EnqueueMessages(params ChatStreamMessage[] messages)
    {
        foreach (var msg in messages)
        {
            _enqueuedMessages.Enqueue(msg);
        }
    }

    public void EnqueueContent(params string[] contents)
    {
        var messageId = Guid.NewGuid().ToString();
        foreach (var content in contents)
        {
            _enqueuedMessages.Enqueue(new ChatStreamMessage { Content = content, MessageId = messageId });
        }

        _enqueuedMessages.Enqueue(new ChatStreamMessage { IsComplete = true, MessageId = messageId });
    }

    public void EnqueueReasoning(params string[] reasonings)
    {
        var messageId = Guid.NewGuid().ToString();
        foreach (var reasoning in reasonings)
        {
            _enqueuedMessages.Enqueue(new ChatStreamMessage { Reasoning = reasoning, MessageId = messageId });
        }
    }

    public void EnqueueError(string error)
    {
        _enqueuedMessages.Enqueue(new ChatStreamMessage { Error = error, IsComplete = true });
    }

    public void SetStreamState(string topicId, StreamState state)
    {
        _streamStates[topicId] = state;
    }

    public void ClearStreamState(string topicId)
    {
        _streamStates.Remove(topicId);
    }

    public IReadOnlySet<string> CancelledTopics => _cancelledTopics;

    // Set to answer not live for every call, the way a transport between connections does.
    public bool NotLive { get; set; }

    // The round trip the send waits on before it holds a stream at all, held open so a test can
    // do something else in that window. Blocking the chunks instead would be a later moment.
    public void HoldTheSendAnswer() => _sendAnswer = new TaskCompletionSource();

    public void LetTheSendAnswer() => _sendAnswer?.TrySetResult();

    public async Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> SendMessageAsync(string topicId, string message,
        string? correlationId = null, AgentConfigPatch? configPatch = null)
    {
        LastConfigPatch = configPatch;
        if (_sendAnswer is not null)
        {
            await _sendAnswer.Task;
        }

        return NotLive
            ? HubResult<IAsyncEnumerable<ChatStreamMessage>>.NotLive
            : HubResult<IAsyncEnumerable<ChatStreamMessage>>.Answered(SendChunks());
    }

    // Answers before the wire would: the real connection opens a hub stream by pulling its
    // first chunk, so a real resume does not come back until the reply next speaks. That
    // timing is modelled one seam down, in FakeHubConnection, and pinned by
    // TopicStreamFlowTests.AResume_WhileTheReplyIsBetweenChunks_ShowsWhatItHasWrittenWithoutWaiting.
    public Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> ResumeStreamAsync(string topicId) =>
        Task.FromResult(NotLive
            ? HubResult<IAsyncEnumerable<ChatStreamMessage>>.NotLive
            : HubResult<IAsyncEnumerable<ChatStreamMessage>>.Answered(ResumeChunks()));

    // The failure stays inside the iteration: a stream that opens and then breaks is a
    // different thing from one that could never open, and both have tests.
    private async IAsyncEnumerable<ChatStreamMessage> SendChunks()
    {
        if (_exceptionToThrow is not null)
        {
            throw _exceptionToThrow;
        }

        if (_blockUntilComplete)
        {
            await _completionSource.Task;
        }

        while (_enqueuedMessages.TryDequeue(out var msg))
        {
            if (StreamDelayMs > 0)
            {
                await Task.Delay(StreamDelayMs);
            }

            yield return msg;
        }
    }

    private async IAsyncEnumerable<ChatStreamMessage> ResumeChunks()
    {
        if (_exceptionToThrow is not null)
        {
            throw _exceptionToThrow;
        }

        if (_blockUntilComplete)
        {
            await _completionSource.Task;
        }

        while (_enqueuedMessages.TryDequeue(out var msg))
        {
            if (StreamDelayMs > 0)
            {
                await Task.Delay(StreamDelayMs);
            }

            yield return msg;
        }
    }

    public Task<HubResult<StreamState>> GetStreamStateAsync(string topicId)
    {
        return Task.FromResult(NotLive
            ? HubResult<StreamState>.NotLive
            : HubResult<StreamState>.Answered(_streamStates.GetValueOrDefault(topicId)));
    }

    public Task<HubResult<Nothing>> CancelTopicAsync(string topicId)
    {
        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _cancelledTopics.Add(topicId);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<bool>> EnqueueMessageAsync(
        string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null)
    {
        LastConfigPatch = configPatch;
        return Task.FromResult(NotLive
            ? HubResult<bool>.NotLive
            : HubResult<bool>.Answered(_enqueueResult));
    }
}